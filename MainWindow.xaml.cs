using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.controles;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyHomelabBrowser
{
    public partial class MainWindow : Window
    {
        // ---------------------------
        // Settings
        // ---------------------------
        readonly SettingsService _settings;
        private string? _remoteChangelogJson;

        // ---------------------------
        // Omnibox
        // ---------------------------
        bool _addressBarEditing;

        // ---------------------------
        // Dock indicator + docking mode
        // ---------------------------
        bool _isDocking;

        // ---------------------------
        // Suspension
        // ---------------------------
        readonly DispatcherTimer _suspendTimer;
        TimeSpan _SuspendDelay = TimeSpan.FromMinutes(5);

        // ---------------------------
        // History / Favorites (persisted)
        // ---------------------------
        readonly List<HistoryEntry> _history = new();
        readonly List<FavoriteItem> _favorites = new();
        CoreWebView2Environment? _privateEnvironment;
        CoreWebView2Environment? _normalEnvironment;
        FlashUxOverlay? _activeOverlay;
        private DispatcherTimer? _backHoldTimer;
        private bool _backHoldTriggered;
        readonly LegacyLauncher _legacyLauncher;
        readonly Dictionary<string, CoreWebView2Environment> _envByProfile = new();
        bool _historyLoaded;
        bool _favoritesLoaded;

        private UpdateService? _updates;
        private bool _isUpdateCheckRunning;
        private Velopack.UpdateInfo? _pendingUpdateInfo;
        private Velopack.UpdateInfo? _downloadedUpdateInfo;

        private readonly ProfileService _profileService = new ProfileService(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MyHomelabBrowser"));

        private readonly CredentialVaultService _vault;

        string _lastHistoryUrl = "";
        DateTime _lastHistoryAt = DateTime.MinValue;

        string HistoryPath => Path.Combine(GetProfileDataDir(), "history.json");
        string FavoritesPath => Path.Combine(GetProfileDataDir(), "favorites.json");

        static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // L'historique peut compter des milliers d'entrées : écriture compacte.
        static readonly JsonSerializerOptions HistoryJsonOpts = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        public MainWindow()
        {
            InitializeComponent();
            DarkTitleBar.Apply(this);

            _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _suspendTimer.Tick += (_, _) => AutoSuspendTabs();

            InitializeToasts();
            InitializeFindBar();
            _ = LoadRemoteChangelogAsync();

            _vault = new CredentialVaultService(() => Path.Combine(AppDataContext.Root, "vault.json.enc"));

            _settings = new SettingsService();
            InitializeAdBlockModule();
            InitializeHomelab();
            InitializeCertificatePrompts();

            _legacyLauncher = new LegacyLauncher(_settings);
            _settings.SettingsChanged += ApplySettings;
            _legacyLauncher.OnDebug += FlashDbg;

            RuntimeLogBuffer.Init();
            RemovedFeatureCleanup.CleanCurrentRoot();

            // Les écritures différées (historique, favoris) visent le dossier du profil
            // courant : on les termine avant que le profil change.
            _profileService.ProfileChanging += FlushPersistentState;
            _profileService.ProfileRenamed += OnProfileRenamed;
            _profileService.ProfileDeleted += OnProfileDeleted;

            _profileService.ProfileChanged += changedProfile =>
            {
                CloseActiveCredentialPrompt();
                _vault.ReloadForCurrentProfile();
                _settings.ReloadForCurrentProfile();
                FlashDomainRules.ReloadForCurrentProfile();
                FlashCompatibilityMemory.ReloadForCurrentProfile();
                RemovedFeatureCleanup.CleanCurrentRoot();

                _historyLoaded = false;
                _favoritesLoaded = false;

                LoadHistory();
                LoadFavorites();

                Dispatcher.Invoke(() =>
                {
                    ReloadHomelabForProfile();
                    RefreshProfileUI();
                    RefreshFavoritesBar();
                    UpdateFavoriteButton();
                    UpdateFillCredentialButtonState();
                });
            };

            Loaded += async (_, _) =>
            {
                try
                {
                    await _adBlock.InitializeAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[AdBlock] Initialisation : " + ex.Message);
                }

                _vault.ReloadForCurrentProfile();
                RefreshProfileUI();

                bool restored = await RestoreSessionIfAnyAsync();
                if (!restored)
                    OpenInitialTab();

                ScheduleBackgroundUpdateCheck();
            };

            PreviewMouseDown += OnGlobalMouseDown;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            Deactivated += (_, _) => HideTransientPopups();
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                    HideTransientPopups();
            };

            try
            {
                _updates = new UpdateService();
            }
            catch
            {
                _updates = null;
            }

            DownloadManager.Instance.RetryByUrl = url =>
            {
                if (!string.IsNullOrWhiteSpace(url))
                    Dispatcher.Invoke(() => CreateTab(url));
            };
            DownloadManager.Instance.ItemsChanged += () => Dispatcher.BeginInvoke(UpdateDownloadsBadge);
            UpdateDownloadsBadge();

            FaviconStore.FaviconUpdated += host => Dispatcher.BeginInvoke(() => OnFaviconStored(host));

            _historyLoaded = false;
            _favoritesLoaded = false;

            LoadHistory();
            LoadFavorites();
            RefreshFavoritesBar();
            UpdateFavoriteButton();

            // Les paramètres chargés au démarrage n'étaient appliqués qu'après une
            // modification : suspension et dossier de téléchargement restaient ignorés.
            ApplySettings(_settings.Settings);
        }

        private async Task LoadRemoteChangelogAsync()
        {
            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };

                _remoteChangelogJson = await http.GetStringAsync(
                    "https://github.com/vazer7070/PommeBrowser-release/releases/latest/download/changelog.json"
                );
            }
            catch
            {
                _remoteChangelogJson = null; // pas bloquant
            }
        }

        // ---------------------------
        // Arbre visuel
        // ---------------------------
        static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t)
                    return t;

                // Si on est dans le Visual Tree
                if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
                {
                    child = VisualTreeHelper.GetParent(child);
                }
                // Sinon (Run, Span, Inline, etc.) → Logical Tree
                else
                {
                    child = LogicalTreeHelper.GetParent(child);
                }
            }

            return null;
        }

        // ---------------------------
        // Dock indicator
        // ---------------------------
        public void BeginDockingMode() => _isDocking = true;

        public void EndDockingMode()
        {
            _isDocking = false;
            HideDockIndicator();
        }

        public void ShowDockIndicator() => DockIndicator.Visibility = Visibility.Visible;
        public void HideDockIndicator() => DockIndicator.Visibility = Visibility.Collapsed;

        // ---------------------------
        // Onglets
        // ---------------------------
        void OpenSettings() => OpenSettingsSection(null);

        static void DetachFromParent(UIElement element)
        {
            if (element == null)
                return;

            var parent = VisualTreeHelper.GetParent(element);

            if (parent is ContentControl cc)
            {
                if (ReferenceEquals(cc.Content, element))
                    cc.Content = null;
                return;
            }

            if (parent is Decorator d)
            {
                if (ReferenceEquals(d.Child, element))
                    d.Child = null;
                return;
            }

            if (parent is Panel p)
            {
                p.Children.Remove(element);
                return;
            }
        }

        bool IsActiveTab(WebTabContent c) =>
            Tabs.SelectedItem is TabItem t && ReferenceEquals(t.Tag, c);

        bool HasAnyPrivateTab()
        {
            return Tabs.Items
                .OfType<TabItem>()
                .Any(t => t.Tag is WebTabContent w && w.IsPrivate);
        }
    }
}