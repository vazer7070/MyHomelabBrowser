using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Session;
using MyHomelabBrowser.controles;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.BrowserSettings;

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
        private OAuthPopupWindow? _oauthPopup;
        private CoreWebView2Environment? _oauthPopupEnvironment;
        private bool _oauthPopupIsPrivate;
        private bool _oauthHooksAttached = false;
        private WebView2? _oauthReturnWeb;
        private bool _oauthFinishing = false;
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

        private readonly SemaphoreSlim _credentialPromptGate = new(1, 1);
        private readonly Dictionary<string, DateTime> _recentCredentialPrompts =
            new(StringComparer.OrdinalIgnoreCase);
        private SaveCredentialDialog? _activeCredentialPrompt;

        public IEnumerable<UserProfile> AllProfiles
            => _profileService.GetAllProfiles();

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

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        public MainWindow()
        {
            InitializeComponent();

            _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _suspendTimer.Tick += (_, _) => AutoSuspendTabs();

            InitializeToasts();
            _ = LoadRemoteChangelogAsync();

            _vault = new CredentialVaultService(() => Path.Combine(AppDataContext.Root, "vault.json.enc"));

            _settings = new SettingsService();
            InitializeAdBlockModule();

            _legacyLauncher = new LegacyLauncher(_settings);
            _settings.SettingsChanged += ApplySettings;
            _legacyLauncher.OnDebug += FlashDbg;

            RuntimeLogBuffer.Init();
            RemovedFeatureCleanup.CleanCurrentRoot();

            // Les écritures différées (historique, favoris) visent le dossier du profil
            // courant : on les termine avant que le profil change.
            _profileService.ProfileChanging += FlushPersistentState;

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

        static readonly DependencyProperty CachedTabPreviewProperty =
    DependencyProperty.RegisterAttached(
        "CachedTabPreview",
        typeof(BitmapSource),
        typeof(MainWindow),
        new PropertyMetadata(null)
    );
        static async System.Threading.Tasks.Task CaptureAndCachePreviewAsync(TabItem tab, WebView2 web)
        {
            if (tab == null || web?.CoreWebView2 == null)
                return;

            // WebView2 pas visible => capture noire
            if (!web.IsVisible)
                return;

            try
            {
                using var stream = new MemoryStream();
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);

                stream.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();

                SetCachedTabPreview(tab, bmp);

                // si le tooltip est déjà visible, on refresh
                if (tab.ToolTip is Border b)
                {
                    if (b.Child is Image img)
                        img.Source = bmp;
                }

                TabPreviewState.SetLastCaptureAt(tab, DateTime.Now);
            }
            catch
            {
                // jamais casser la nav
            }
        }

        static void SetCachedTabPreview(TabItem tab, BitmapSource? bmp)
            => tab.SetValue(CachedTabPreviewProperty, bmp);

        static BitmapSource? GetCachedTabPreview(TabItem tab)
            => tab.GetValue(CachedTabPreviewProperty) as BitmapSource;

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

        void SwitchProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi ||
                mi.DataContext is not UserProfile profile)
                return;

            var dlg = new LoginDialog(profile.Username)
            {
                Owner = this,
                Title = "Changer de profil",
                ValidateLogin = (_, p) => _profileService.VerifyPassword(profile, p),
                FailureMessageProvider = _ => BuildLoginFailureMessage(profile)
            };

            if (dlg.ShowDialog() != true)
                return;

            _profileService.LoginSilent(profile);
            RefreshProfileUI();
        }

        static string? BuildLoginFailureMessage(UserProfile? profile)
        {
            if (profile?.LoginLockUntilUtc is DateTime until && until > DateTime.UtcNow)
                return $"Trop de tentatives. Réessayez après {until.ToLocalTime():HH:mm:ss}.";

            return null;
        }

        static string GetProfileInitial(string? username)
        {
            string value = (username ?? string.Empty).Trim();
            return value.Length == 0 ? "?" : value[..1].ToUpperInvariant();
        }

        void PopulateSwitchProfileMenu(MenuItem parent)
        {
            parent.Items.Clear();

            foreach (var profile in _profileService.GetAllProfiles())
            {
                if (_profileService.Current != null &&
                    profile.Username.Equals(_profileService.Current.Username,
                                             StringComparison.OrdinalIgnoreCase))
                    continue;

                var username = profile.Username;

                var item = new MenuItem
                {
                    DataContext = profile,
                    Header = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            CreateAvatar(username, 24, 11),
                            new TextBlock
                            {
                                Text = username,
                                Margin = new Thickness(10, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        }
                    }
                };

                item.Click += SwitchProfile_Click;
                parent.Items.Add(item);
            }

            if (parent.Items.Count == 0)
            {
                parent.Items.Add(new MenuItem
                {
                    Header = "Aucun autre profil",
                    IsEnabled = false
                });
            }
        }

        Border CreateAvatar(string username, double size, double fontSize)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = GetAvatarBrush(username),
                Child = new TextBlock
                {
                    Text = GetProfileInitial(username),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = fontSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        void RefreshProfileUI()
        {
            var current = _profileService.Current;
            bool loggedIn = current != null && !string.IsNullOrWhiteSpace(current.Username);

            if (!loggedIn)
            {
                ProfileButton.Content = "👤";
                ProfileButton.ClearValue(BackgroundProperty);
                ProfileButton.ToolTip = "Profils : se connecter ou créer un profil";
                return;
            }

            ProfileButton.Content = GetProfileInitial(current!.Username);
            ProfileButton.Background = GetAvatarBrush(current.Username);
            ProfileButton.ToolTip = $"Profil : {current.Username}";
        }

        void Profile_Login_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LoginDialog
            {
                Owner = this,
                Title = "Connexion",
                ValidateLogin = (u, p) =>
                {
                    var profile = _profileService.FindProfile(u);
                    return profile != null && _profileService.VerifyPassword(profile, p);
                },
                FailureMessageProvider = u => BuildLoginFailureMessage(_profileService.FindProfile(u))
            };

            if (dlg.ShowDialog() != true)
                return;

            var prof = _profileService.FindProfile(dlg.Username);
            if (prof == null)
                return;

            _profileService.LoginSilent(prof);
            RefreshProfileUI();
        }

        void Profile_Create_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CreateProfileDialog
            {
                Owner = this,
                Title = "Créer un profil",
                UsernameExists = _profileService.ProfileExists
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                _profileService.CreateProfile(dlg.Username, dlg.Password);
                _profileService.Login(dlg.Username, dlg.Password);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, "Créer un profil", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshProfileUI();
        }

        static readonly Brush[] AvatarBrushes = CreateAvatarBrushes();

        static Brush[] CreateAvatarBrushes()
        {
            string[] colors = { "#FF3A6EA5", "#FF8E44AD", "#FF1F9D55", "#FFD9731A", "#FFD64545", "#FF16A085", "#FF5B6CE0" };
            var brushes = new Brush[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(colors[i])!;
                brush.Freeze();
                brushes[i] = brush;
            }
            return brushes;
        }

        static Brush GetAvatarBrush(string username)
        {
            // string.GetHashCode est aléatoire à chaque lancement en .NET :
            // la couleur de l'avatar changeait à chaque démarrage.
            uint hash = 2166136261;
            foreach (char c in (username ?? string.Empty).Trim().ToLowerInvariant())
                hash = (hash ^ c) * 16777619;

            return AvatarBrushes[hash % (uint)AvatarBrushes.Length];
        }

        private async Task HandleCredentialCandidateAsync(CredentialCandidate candidate)
        {
            if (candidate == null ||
                string.IsNullOrWhiteSpace(candidate.Origin) ||
                string.IsNullOrEmpty(candidate.Password))
            {
                return;
            }

            var promptKey = BuildCredentialPromptKey(candidate);

            if (IsCredentialPromptCoolingDown(promptKey))
                return;

            if (!await _credentialPromptGate.WaitAsync(0))
                return;

            try
            {
                if (Dispatcher.CheckAccess())
                {
                    await HandleCredentialCandidateOnUiAsync(candidate);
                }
                else
                {
                    await Dispatcher
                        .InvokeAsync(() => HandleCredentialCandidateOnUiAsync(candidate))
                        .Task
                        .Unwrap();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Vault] Erreur pendant la demande d'enregistrement : " + ex.Message);
            }
            finally
            {
                RememberCredentialPrompt(promptKey);
                _credentialPromptGate.Release();
            }
        }

        private async Task HandleCredentialCandidateOnUiAsync(CredentialCandidate candidate)
        {
            var profileAtCapture = _profileService.Current?.Username;
            if (string.IsNullOrWhiteSpace(profileAtCapture))
                return;

            if (!EnsureVaultAvailableAndUnlocked())
                return;

            var policy = _vault.GetPolicy(candidate.Origin);
            if (policy == CredentialSavePolicy.NeverSave)
                return;

            var existing = _vault.FindForOrigin(candidate.Origin, candidate.Username);
            if (existing != null &&
                string.Equals(existing.Password, candidate.Password, StringComparison.Ordinal))
            {
                return;
            }

            if (policy == CredentialSavePolicy.AlwaysSave)
            {
                _vault.Upsert(
                    candidate.Origin,
                    candidate.Username,
                    candidate.Password,
                    candidate.FormAction,
                    alwaysSave: true);

                ShowToast(
                    existing == null ? "Identifiant enregistré" : "Identifiant mis à jour",
                    CredentialOrigin.DisplayName(candidate.Origin),
                    null);
                return;
            }

            var dialog = new SaveCredentialDialog(
                CredentialOrigin.DisplayName(candidate.Origin),
                candidate.Username);

            _activeCredentialPrompt = dialog;

            SaveCredentialDialogResult result;
            try
            {
                result = await dialog.ShowAsync(this);
            }
            finally
            {
                if (ReferenceEquals(_activeCredentialPrompt, dialog))
                    _activeCredentialPrompt = null;
            }

            if (!string.Equals(
                    _profileService.Current?.Username,
                    profileAtCapture,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (result.Decision == SaveCredentialDecision.NeverSave)
            {
                _vault.SetPolicy(candidate.Origin, CredentialSavePolicy.NeverSave);
                ShowToast(
                    "Enregistrement désactivé",
                    CredentialOrigin.DisplayName(candidate.Origin),
                    null);
                return;
            }

            if (result.Decision != SaveCredentialDecision.Save)
                return;

            _vault.Upsert(
                candidate.Origin,
                candidate.Username,
                candidate.Password,
                candidate.FormAction,
                alwaysSave: result.AlwaysSave);

            if (!result.AlwaysSave)
                _vault.SetPolicy(candidate.Origin, CredentialSavePolicy.Ask);

            ShowToast(
                existing == null ? "Mot de passe enregistré" : "Mot de passe mis à jour",
                CredentialOrigin.DisplayName(candidate.Origin),
                null);
        }

        private static string BuildCredentialPromptKey(CredentialCandidate candidate)
        {
            return string.Join(
                "|",
                candidate.Origin.Trim().ToLowerInvariant(),
                candidate.Username.Trim().ToLowerInvariant(),
                (candidate.FormAction ?? string.Empty).Trim().ToLowerInvariant());
        }

        private bool IsCredentialPromptCoolingDown(string key)
        {
            var now = DateTime.UtcNow;

            lock (_recentCredentialPrompts)
            {
                foreach (var expired in _recentCredentialPrompts
                             .Where(x => now - x.Value > TimeSpan.FromSeconds(30))
                             .Select(x => x.Key)
                             .ToArray())
                {
                    _recentCredentialPrompts.Remove(expired);
                }

                return _recentCredentialPrompts.TryGetValue(key, out var lastShown) &&
                       now - lastShown < TimeSpan.FromSeconds(15);
            }
        }

        private void RememberCredentialPrompt(string key)
        {
            lock (_recentCredentialPrompts)
                _recentCredentialPrompts[key] = DateTime.UtcNow;
        }

        private void CloseActiveCredentialPrompt()
        {
            var dialog = _activeCredentialPrompt;
            _activeCredentialPrompt = null;

            try
            {
                dialog?.CancelAndClose();
            }
            catch
            {
                // Un changement de profil ne doit jamais casser l'interface.
            }
        }

        private bool EnsureVaultAvailableAndUnlocked()
        {
            if (_profileService.Current == null)
                return false;

            if (_vault.IsUnlocked)
                return true;

            if (!_vault.VaultExists)
            {
                var first = new SimplePasswordDialog("Créer le mot de passe du coffre")
                {
                    Owner = this
                };

                if (first.ShowDialog() != true || string.IsNullOrWhiteSpace(first.Password))
                    return false;

                var confirmation = new SimplePasswordDialog("Confirmer le mot de passe du coffre")
                {
                    Owner = this
                };

                if (confirmation.ShowDialog() != true)
                    return false;

                if (!string.Equals(first.Password, confirmation.Password, StringComparison.Ordinal))
                {
                    MessageBox.Show(
                        this,
                        "Les mots de passe ne correspondent pas.",
                        "Coffre des mots de passe",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                if (!_vault.TryInitializeNewVault(first.Password))
                {
                    MessageBox.Show(
                        this,
                        "Impossible de créer le coffre.",
                        "Coffre des mots de passe",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return false;
                }

                return true;
            }

            var unlockDialog = new UnlockVaultDialog
            {
                Owner = this
            };

            if (unlockDialog.ShowDialog() != true)
                return false;

            if (_vault.TryUnlock(unlockDialog.EnteredPassword))
                return true;

            var lockedUntil = _vault.UnlockAvailableAtUtc;
            var message = lockedUntil.HasValue && lockedUntil.Value > DateTime.UtcNow
                ? $"Le coffre est temporairement verrouillé jusqu’à {lockedUntil.Value.ToLocalTime():HH:mm:ss}."
                : "Mot de passe du coffre incorrect.";

            MessageBox.Show(
                this,
                message,
                "Coffre des mots de passe",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        void Profile_Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_profileService.Current == null)
                return;

            var dlg = new ProfileSettingsDialog(_profileService, _vault)
            {
                Owner = this
            };

            dlg.ShowDialog();
            RefreshProfileUI();

            // Le dialogue peut avoir déverrouillé ou modifié le coffre.
            UpdateFillCredentialButtonState();
        }

        void Profile_Logout_Click(object sender, RoutedEventArgs e)
        {
            if (_profileService.Current == null)
                return;

            _profileService.Logout();

        }

        /// <summary>
        /// La fenêtre ouverte par window.open doit partager l'environnement WebView2
        /// de l'onglet qui l'ouvre (profil ou navigation privée) : WebView2 refuse un
        /// NewWindow issu d'un autre environnement, et la session de connexion doit
        /// arriver dans les cookies de l'onglet d'origine.
        /// </summary>
        private async Task<OAuthPopupWindow> GetOrCreateOAuthPopupAsync(CoreWebView2Environment environment, bool isPrivate)
        {
            if (_oauthPopup != null &&
                (!ReferenceEquals(_oauthPopupEnvironment, environment) || _oauthPopupIsPrivate != isPrivate))
            {
                var previous = _oauthPopup;
                _oauthPopup = null;
                try { previous.Close(); } catch { }
            }

            if (_oauthPopup == null)
            {
                var popup = new OAuthPopupWindow { Owner = this };
                _oauthPopup = popup;
                _oauthPopupEnvironment = environment;
                _oauthPopupIsPrivate = isPrivate;
                _oauthHooksAttached = false;

                popup.Closed += (_, __) =>
                {
                    if (!ReferenceEquals(_oauthPopup, popup))
                        return;

                    _oauthPopup = null;
                    _oauthPopupEnvironment = null;
                    _oauthHooksAttached = false;
                    _oauthFinishing = false;
                    _oauthReturnWeb = null;
                };
            }

            if (!_oauthPopup.IsVisible)
                _oauthPopup.Show();

            await _oauthPopup.EnsureReadyAsync(
                environment,
                isPrivate ? CreatePrivateControllerOptions(environment) : null);

            if (!_oauthHooksAttached && _oauthPopup.Web?.CoreWebView2 != null)
            {
                _oauthHooksAttached = true;
                var popupWindow = _oauthPopup;

                popupWindow.Web.SourceChanged += async (_, __) =>
                {
                    try
                    {
                        var u = popupWindow.Web.Source?.ToString() ?? "";
                        FlashDbg($"[OAuthPopup] SourceChanged: {u}");

                        // Fin du parcours Gameforge : la page /message transmet le jeton
                        // à l'onglet d'origine puis la fenêtre peut disparaître.
                        bool done =
                            u.Contains("gameforge.com/service/external-auth", StringComparison.OrdinalIgnoreCase) &&
                            u.EndsWith("/message", StringComparison.OrdinalIgnoreCase);

                        if (!done || _oauthFinishing)
                            return;

                        _oauthFinishing = true;
                        await Task.Delay(500);
                        popupWindow.Hide();
                        await Task.Delay(200);
                        _oauthFinishing = false;
                    }
                    catch { }
                };
            }

            return _oauthPopup;
        }

        /// <summary>
        /// Détourne un window.open vers la fenêtre de connexion. Retourne false si
        /// WebView2 doit finalement gérer la fenêtre lui-même.
        /// </summary>
        private async Task<bool> TryRouteToPopupAsync(WebView2 opener, CoreWebView2NewWindowRequestedEventArgs ev)
        {
            var environment = opener.CoreWebView2?.Environment;
            if (environment == null)
                return false;

            bool isPrivate = opener.CoreWebView2!.Profile.IsInPrivateModeEnabled;
            _oauthReturnWeb = opener;

            var popup = await GetOrCreateOAuthPopupAsync(environment, isPrivate);
            if (popup.Web?.CoreWebView2 == null)
                return false;

            if (!popup.IsVisible)
                popup.Show();

            ev.NewWindow = popup.Web.CoreWebView2;
            ev.Handled = true;
            return true;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        // ---------------------------
        // Settings apply
        // ---------------------------

        // ---------------------------
        // Address bar sync
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
        // Tabs / WebHost
        // ---------------------------

        void OpenReportIssueView(ReportIssueOptions options)
        {
            // ===============================
            // 🔍 CONTEXTE NAVIGATION ACTUEL
            // ===============================
            WebTabContent? wt = null;

            // 1️⃣ priorité ABSOLUE : onglet sélectionné SI c’est un WebTabContent
            if (Tabs.SelectedItem is TabItem selected &&
                selected.Tag is WebTabContent webTab)
            {
                wt = webTab;
            }
            else
            {
                // 2️⃣ fallback : dernier onglet Web existant
                wt = Tabs.Items
                    .OfType<TabItem>()
                    .Select(t => t.Tag)
                    .OfType<WebTabContent>()
                    .LastOrDefault();
            }

            if (wt != null && !wt.IsCustomView)
            {
                string url =
                    wt.IsLegacyExternal && !string.IsNullOrWhiteSpace(wt.LegacyUrl)
                        ? wt.LegacyUrl
                        : wt.Web?.Source?.AbsoluteUri ?? "inconnu";

                string title =
                    wt.IsLegacyExternal
                        ? "Legacy (Basilisk)"
                        : wt.Web?.CoreWebView2?.DocumentTitle ?? "inconnu";

                string flashMode =
                    wt.IsLegacyExternal ? "legacy" :
                    wt.FlashMode == FlashMode.Ruffle ? "ruffle" :
                    wt.FlashMode == FlashMode.Legacy ? "legacy" :
                    "auto";

                // Un onglet privé ne laisse jamais son adresse partir dans un rapport.
                options.Context = new BrowserContext
                {
                    IsPrivate = wt.IsPrivate,
                    IsLegacy = wt.IsLegacyExternal,
                    CurrentUrl = wt.IsPrivate ? null : url,
                    PageTitle = wt.IsPrivate ? null : title,
                    FlashMode = flashMode
                };
            }
            else
            {
                options.Context = null;
            }

            // ===============================
            // 🔁 ANTI DOUBLON
            // ===============================
            foreach (TabItem t in Tabs.Items)
            {
                if (t.Tag is ViewTabContent v && v.View is ReportIssueView)
                {
                    Tabs.SelectedItem = t;
                    SyncWebHostWithSelection();
                    return;
                }
            }

            // ===============================
            // 🆕 ONGLET REPORT
            // ===============================
            var view = new ReportIssueView(options);

            view.CloseRequested += () =>
            {
                var tabToClose = Tabs.Items
                    .OfType<TabItem>()
                    .FirstOrDefault(t => t.Tag is ViewTabContent vc && vc.View == view);

                if (tabToClose != null)
                    CloseTab(tabToClose);
            };

            // OpenViewTab branche aussi la croix de fermeture, absente auparavant.
            OpenViewTab(view, "Signaler un problème");
        }
        void OpenSettings() => OpenSettingsSection(null);

        void OpenSettingsSection(string? sectionName)
        {
            // ✅ Si déjà ouvert : sélectionner UNIQUEMENT l'onglet Settings
            foreach (TabItem t in Tabs.Items)
            {
                if (t.Tag is ViewTabContent v && v.View is SettingsView existingSettings)
                {
                    if (!string.IsNullOrWhiteSpace(sectionName))
                        existingSettings.SelectSection(sectionName);

                    Tabs.SelectedItem = t;
                    SyncWebHostWithSelection();
                    return;
                }
            }

            // ✅ Sinon créer Settings
            var view = new SettingsView(_settings);
            if (!string.IsNullOrWhiteSpace(sectionName))
                view.SelectSection(sectionName);

            // ✅ history
            view.OpenHistoryRequested += OpenHistory;
            view.OpenReportIssueRequested += OpenReportIssueView;

            WireUpdateActions(view);

            OpenViewTab(view, "Paramètres");
        }

        /// <summary>
        /// Ouvre une vue interne (paramètres, historique, rapport) dans un onglet.
        /// </summary>
        TabItem OpenViewTab(UserControl view, string title)
        {
            var header = new BrowserTabHeader();
            header.SetTitle(title);
            header.SetIcon(StartTabIcon.Value);

            var tab = new TabItem
            {
                Header = header,
                Tag = new ViewTabContent { View = view }
            };

            header.CloseRequested += () => CloseTab(tab);
            header.ReorderRequested += dir => ReorderTab(tab, dir);

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();
            return tab;
        }
        void FillCredential_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent content)
                return;

            var web = content.Web;
            if (web?.Source == null ||
                !CredentialOrigin.TryCreateTrusted(web.Source, out _))
                return;

            if (!EnsureVaultAvailableAndUnlocked())
                return;

            var credential = _vault.FindForOrigin(web.Source);
            if (credential == null)
            {
                ShowToast(
                    "Aucun identifiant",
                    "Aucun compte n’est enregistré pour cette origine.",
                    null);
                UpdateFillCredentialButtonState();
                return;
            }

            var usernameJson = JsonSerializer.Serialize(credential.Username);
            var passwordJson = JsonSerializer.Serialize(credential.Password);

            var script = $$"""
            (() => {
              let username = {{usernameJson}};
              let password = {{passwordJson}};

              function isUsable(element) {
                if (!element || element.disabled || element.readOnly) return false;
                const style = window.getComputedStyle(element);
                return style.display !== 'none' && style.visibility !== 'hidden';
              }

              function setValue(element, value) {
                if (!isUsable(element) || !value) return;
                element.focus();
                const descriptor = Object.getOwnPropertyDescriptor(
                  HTMLInputElement.prototype,
                  'value'
                );
                descriptor?.set?.call(element, value);
                element.dispatchEvent(new Event('input', { bubbles: true }));
                element.dispatchEvent(new Event('change', { bubbles: true }));
              }

              const passwordFields = Array.from(
                document.querySelectorAll('input[type="password"]')
              ).filter(isUsable);

              if (passwordFields.length === 0) return;
              if (passwordFields.some(field =>
                    (field.autocomplete || '').toLowerCase() === 'new-password')) return;

              const passwordField =
                passwordFields.find(field =>
                  (field.autocomplete || '').toLowerCase() === 'current-password') ||
                (passwordFields.length === 1 ? passwordFields[0] : null);

              if (!passwordField) return;

              const root = passwordField.form || document;
              const usernameField =
                root.querySelector('input[autocomplete="username"]') ||
                root.querySelector('input[type="email"]') ||
                root.querySelector('input[name*="user" i], input[id*="user" i]') ||
                root.querySelector('input[name*="email" i], input[id*="email" i]') ||
                root.querySelector('input[type="text"]:not([name*="search" i]):not([id*="search" i])');

              if (usernameField && !usernameField.value.trim())
                setValue(usernameField, username);

              if (!passwordField.value)
                setValue(passwordField, password);

              username = '';
              password = '';
            })();
            """;

            _ = web.ExecuteScriptAsync(script);
        }

        void UpdateFillCredentialButtonState()
        {
            try
            {
                FillCredentialButton.IsEnabled = false;
                FillCredentialButton.Opacity = 0.35;
                FillCredentialButton.ToolTip = "Aucun identifiant disponible";

                if (_profileService.Current == null)
                    return;

                if (Tabs.SelectedItem is not TabItem tab)
                    return;

                if (tab.Tag is not WebTabContent content)
                    return;

                var source = content.Web?.Source;
                if (source == null)
                    return;

                if (!CredentialOrigin.TryCreateTrusted(source, out _))
                    return;

                if (!_vault.IsUnlocked)
                {
                    if (_vault.VaultExists)
                    {
                        FillCredentialButton.IsEnabled = true;
                        FillCredentialButton.Opacity = 1.0;
                        FillCredentialButton.ToolTip =
                            "Déverrouiller le coffre pour rechercher un identifiant";
                    }

                    return;
                }

                if (_vault.HasCredentialForOrigin(source))
                {
                    FillCredentialButton.IsEnabled = true;
                    FillCredentialButton.Opacity = 1.0;
                    FillCredentialButton.ToolTip = "Remplir les identifiants";
                }
            }
            catch
            {
                // L'état du bouton ne doit jamais casser l'interface.
            }
        }

        public bool CanUseFlashOrLegacy(Uri uri, bool flashDetected, out string reason)
        {
            reason = "";

            // Pas de Flash → aucune contrainte
            if (!flashDetected)
                return true;

            var s = _settings.Settings;

            if (!s.EnableFlashSupport)
            {
                reason = "Le support Flash est désactivé dans les paramètres.";
                return false;
            }

            var rule = FlashDomainRules.GetRule(uri);

            bool legacyRequired = rule == FlashRuleMode.Legacy;

            if (legacyRequired && !_legacyLauncher.CanLaunch())
            {
                reason = "Ce site nécessite Flash réel, mais Basilisk n’est pas configuré.";
                return false;
            }

            return true;
        }

        private void HideLegacyWindowsExcept(WebTabContent? keep)
        {
            foreach (var item in Tabs.Items)
            {
                if (item is not TabItem ti) continue;
                if (ti.Tag is not WebTabContent wt) continue;

                // ✅ Ne pas toucher à l'onglet actif legacy
                if (keep != null && ReferenceEquals(wt, keep))
                    continue;

                // ✅ On cache uniquement les fenêtres legacy des autres onglets
                if (wt.LegacyHwnd != IntPtr.Zero)
                    ShowWindow(wt.LegacyHwnd, SW_HIDE);

                if (wt.LegacyTopHwnd != IntPtr.Zero && wt.LegacyTopHwnd == wt.LegacyHwnd)
                    ShowWindow(wt.LegacyTopHwnd, SW_HIDE);
            }
        }

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

        // ---------------------------
        // Favorites bar + star button (safe until XAML exists)
        // ---------------------------

        void AddFavorite(FavoriteItem fav)
        {
            // éviter doublons
            if (_favorites.Any(f => string.Equals(f.Url, fav.Url, StringComparison.OrdinalIgnoreCase)))
                return;

            _favorites.Add(fav);
            SaveFavorites();

            RefreshFavoritesBar();
            UpdateFavoriteButton();
        }

        void RemoveFavorite(FavoriteItem fav)
        {
            _favorites.Remove(fav);
            SaveFavorites();

            RefreshFavoritesBar();
            UpdateFavoriteButton();
        }

        // ---------------------------
        // History
        // ---------------------------

        // ---------------------------
        // Reorder / pin
        // ---------------------------

        // ---------------------------
        // Suspension
        // ---------------------------

        private IntPtr FindBestEmbedChild(IntPtr top)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            void Scan(IntPtr parent)
            {
                EnumChildWindows(parent, (h, _) =>
                {
                    if (!IsWindowVisible(h)) return true;

                    if (GetWindowRect(h, out var rc))
                    {
                        int w = rc.Right - rc.Left;
                        int hgt = rc.Bottom - rc.Top;

                        if (w > 10 && hgt > 10)
                        {
                            long area = (long)w * hgt;

                            if (area > bestArea)
                            {
                                bestArea = area;
                                best = h;
                            }
                        }
                    }

                    // ✅ descend dans tous les enfants
                    Scan(h);

                    return true;
                }, IntPtr.Zero);
            }

            Scan(top);
            return best;
        }

        // ---------------------------
        // Navigation / Omnibox
        // ---------------------------

        // ---------------------------
        // Sidebar
        // ---------------------------

        static void AttachPreview(TabItem tab, WebView2 web)
        {
            if (tab == null || web == null)
                return;

            // ✅ ne pas rehacker 50 fois
            if (TabPreviewState.GetIsHooked(tab))
                return;

            TabPreviewState.SetIsHooked(tab, true);

            // Tooltip container (une seule fois)
            var border = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Width = 320,
                Height = 200
            };
            tab.ToolTip = border;

            tab.MouseLeave += (_, _) =>
            {
                // ✅ annule si on quitte
                var cts = TabPreviewState.GetCts(tab);
                if (cts != null)
                {
                    try { cts.Cancel(); } catch { }
                    try { cts.Dispose(); } catch { }
                    TabPreviewState.SetCts(tab, null);
                }
            };

            tab.MouseEnter += async (_, _) =>
            {
                if (web.CoreWebView2 == null)
                    return;

                // Tooltip content (Image) créé une fois
                if (tab.ToolTip is Border bb && bb.Child == null)
                {
                    bb.Child = new Image
                    {
                        Width = 320,
                        Height = 200,
                        Stretch = Stretch.UniformToFill
                    };
                }

                // 1) Affiche immédiatement le cache si présent
                var cached = GetCachedTabPreview(tab);
                if (cached != null && tab.ToolTip is Border b1 && b1.Child is Image img1)
                    img1.Source = cached;

                // 2) si le WebView n'est PAS visible => ne pas capturer (sinon noir)
                if (!web.IsVisible)
                    return;

                // 3) cooldown : max 1 capture / 2s
                var last = TabPreviewState.GetLastCaptureAt(tab);
                if ((DateTime.Now - last) < TimeSpan.FromSeconds(2))
                    return;

                // 4) annule ancien job
                var old = TabPreviewState.GetCts(tab);
                if (old != null)
                {
                    try { old.Cancel(); } catch { }
                    try { old.Dispose(); } catch { }
                }

                var cts = new CancellationTokenSource();
                TabPreviewState.SetCts(tab, cts);

                try
                {
                    await System.Threading.Tasks.Task.Delay(180, cts.Token);
                    if (cts.IsCancellationRequested || web.CoreWebView2 == null)
                        return;

                    // Capture + cache (et refresh tooltip si visible)
                    await CaptureAndCachePreviewAsync(tab, web);
                }
                catch
                {
                }
                finally
                {
                    var cur = TabPreviewState.GetCts(tab);
                    if (ReferenceEquals(cur, cts))
                    {
                        try { cts.Dispose(); } catch { }
                        TabPreviewState.SetCts(tab, null);
                    }
                }
            };

        }

        // ---------------------------
        // Settings button handler
        // ---------------------------
        private void OpenSettings_Click(object sender, RoutedEventArgs e)
            => OpenSettings();

        // ---------------------------
        // Detach / Redock
        // ---------------------------

        // ---------------------------
        // Détacher / réancrer
        // ---------------------------
        void DetachTab(TabItem tab)
        {
            if (tab.Tag is not WebTabContent state)
                return;

            bool wasSelected = Equals(Tabs.SelectedItem, tab);
            if (wasSelected)
                SelectFallbackTab(tab);

            // Page d'accueil : une nouvelle page est ouverte dans une fenêtre séparée.
            if (state.IsCustomView)
            {
                Tabs.Items.Remove(tab);
                OpenDetachedCustomTab(state);
                EnsureAtLeastOneTab();
                return;
            }

            if (state.Web == null)
                return;

            Tabs.Items.Remove(tab);

            // Fenêtre détachée créée avant de toucher au WebView.
            var win = new DetachedWindow(this, state)
            {
                Owner = this,
                RequestRedock = RedockWebTab
            };

            if (ReferenceEquals(WebHost.Content, state.HostGrid))
                WebHost.Content = null;

            win.Show();
            EnsureAtLeastOneTab();
            SyncWebHostWithSelection();
        }

        void EnsureAtLeastOneTab()
        {
            if (Tabs.Items.Count == 0)
                CreateEmptyStartTab();
        }

        void OpenDetachedCustomTab(WebTabContent content)
        {
            var win = new DetachedCustomWindow();
            win.SetContent(CreateStartPageView(null));

            // Au réancrage, un onglet d'accueil complet (fermable, déplaçable) est recréé.
            win.RequestRedock += _ => CreateEmptyStartTab();
            win.Show();
        }

        bool IsActiveTab(WebTabContent c) =>
    Tabs.SelectedItem is TabItem t && ReferenceEquals(t.Tag, c);

        private async Task<bool> TryLaunchLegacy(
     WebTabContent content,
     Uri uri,
     BrowserTabHeader header)
        {
            try
            {
                if (!_legacyLauncher.CanLaunch())
                {
                    content.LegacyLastError = "Basilisk n’est pas configuré ou chemin invalide.";
                    return false;
                }

                return await LaunchLegacyIntoInternalTabAsync(content, uri, header);
            }
            catch (Exception ex)
            {
                content.LegacyLastError = ex.ToString();
                return false;
            }
        }

        async Task HandleFlashAsync(
            WebView2 web,
            WebTabContent content,
            BrowserTabHeader header,
            FlashUxOverlay overlay,
            bool forceRecheck = false)
        {
            if (web.Source == null || content.FlashService == null || web.CoreWebView2 == null)
                return;

            Uri uri = web.Source;
            FlashDbg($"HandleFlashAsync ENTER url={uri} force={forceRecheck}");

            FlashRuleMode rule = FlashDomainRules.GetRule(uri);
            bool forceLegacy = rule == FlashRuleMode.Legacy || content.ForceLegacyOnce;
            content.ForceLegacyOnce = false;

            if (rule == FlashRuleMode.Disabled)
            {
                content.FlashChecked = true;
                content.FlashRequired = false;
                content.LastFlashDetection = FlashDetectionResult.None;
                content.FlashMode = FlashMode.None;
                StopRuffleMonitoring(content);
                overlay.Hide();
                ApplyLegacyRuleToMode(content);
                return;
            }

            async Task LaunchLegacyExplicitAsync()
            {
                if (!_legacyLauncher.CanLaunch())
                {
                    overlay.BindHost(content.HostGrid);
                    overlay.ShowBlocked("Le moteur Legacy n'est pas configuré.");
                    overlay.SetActions("Configurer Basilisk", OpenSettings);
                    return;
                }

                StopRuffleMonitoring(content);
                content.FlashMode = FlashMode.Legacy;
                overlay.BindHost(content.HostGrid);
                overlay.TryShow("Ouverture manuelle avec le moteur Legacy…");
                ApplyLegacyRuleToMode(content);

                bool ok = await TryLaunchLegacy(content, uri, header).ConfigureAwait(true);
                if (ok)
                {
                    FlashCompatibilityMemory.RecordLegacySuccess(
                        uri,
                        content.LastFlashDetection);
                    overlay.Hide();
                    SyncWebHostWithSelection();
                }
                else
                {
                    overlay.ShowBlocked(
                        "Impossible de lancer Basilisk.\n\n" +
                        (content.LegacyLastError ?? "Erreur inconnue."));
                    overlay.SetActions("Réessayer", () => _ = LaunchLegacyExplicitAsync(),
                        "Paramètres", OpenSettings);
                }
            }

            void ShowRuffleFailure(string reason)
            {
                content.RuffleFailureReason = reason;
                content.RuffleFailureStatus ??= content.RuffleMonitor?.LastStatus;
                if (!IsActiveTab(content))
                    return;

                overlay.BindHost(content.HostGrid);
                overlay.ShowBlocked(FlashCompatibilityPolicy.BuildRuffleFailureMessage(
                    content.LastFlashDetection, reason, content.RuffleFailureStatus));

                if (_legacyLauncher.CanLaunch())
                {
                    overlay.SetActions(
                        "Ouvrir avec le moteur Legacy",
                        () => _ = LaunchLegacyExplicitAsync(),
                        "Réessayer Ruffle",
                        () => _ = HandleFlashAsync(web, content, header, overlay, forceRecheck: true));
                }
                else
                {
                    overlay.SetActions(
                        "Réessayer Ruffle",
                        () => _ = HandleFlashAsync(web, content, header, overlay, forceRecheck: true),
                        "Configurer Basilisk",
                        OpenSettings);
                }
            }

            if (forceLegacy)
            {
                await LaunchLegacyExplicitAsync().ConfigureAwait(true);
                return;
            }

            if (!forceRecheck && content.FlashChecked && !content.FlashRequired)
                return;

            FlashDetectionResult detection = await content.FlashService.DetectAsync().ConfigureAwait(true);

            content.FlashChecked = true;
            content.FlashRequired = detection.Detected;
            content.LastFlashDetection = detection;
            ApplyLegacyRuleToMode(content);

            if (!detection.Detected)
            {
                if (IsActiveTab(content))
                    overlay.Hide();
                return;
            }

            if (IsActiveTab(content))
                overlay.BindHost(content.HostGrid);

            if (!CanUseFlashOrLegacy(uri, flashDetected: true, out string reason))
            {
                overlay.ShowBlocked(reason);
                overlay.SetActions("Paramètres", OpenSettings);
                return;
            }

            FlashMode mode = content.FlashService.DecideInitialMode(
                uri, forceLegacyOnce: false, detection);
            content.FlashMode = mode;

            if (mode == FlashMode.Legacy)
            {
                await LaunchLegacyExplicitAsync().ConfigureAwait(true);
                return;
            }

            if (IsActiveTab(content))
                overlay.TryShow("Flash détecté — démarrage du moteur Ruffle intégré…\n" + detection.Describe());
            content.FlashMode = FlashMode.Ruffle;
            StopRuffleMonitoring(content);

            content.RuffleFailureReason = null;
            content.RuffleFailureStatus = null;
            RuffleInjectionResult injection = await RuffleInjector.InjectAsync(web, detection).ConfigureAwait(true);
            if (!injection.Success)
            {
                string injectionReason = injection.Error ?? injection.Status;
                FlashCompatibilityMemory.RecordRuffleFailure(uri, detection, injectionReason);
                ShowRuffleFailure(injectionReason);
                return;
            }

            content.RuffleMonitor = new RuffleMonitor(web);
            content.RuffleMonitor.ReadyDetected += status =>
            {
                content.RuffleFailureReason = null;
                content.RuffleFailureStatus = null;
                FlashCompatibilityMemory.RecordRuffleSuccess(uri, detection);
                Dispatcher.Invoke(() =>
                {
                    if (IsActiveTab(content))
                        overlay.Hide();
                });
            };

            content.RuffleMonitor.FailureDetected += failure =>
            {
                content.RuffleFailureStatus = failure.Status;
                FlashCompatibilityMemory.RecordRuffleFailure(uri, detection, failure.Reason);
                Dispatcher.Invoke(() => ShowRuffleFailure(failure.Reason));
            };

            content.RuffleMonitor.Start();
            UpdateManualLegacyButton();
        }

        private void ManualLegacyButton_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent content ||
                content.Web?.Source == null)
                return;

            var uri = content.Web.Source;

            // déjà en legacy => rien (et bouton déjà disabled normalement)
            bool alreadyLegacy =
                content.FlashMode == FlashMode.Legacy ||
                content.IsLegacyExternal ||
                FlashDomainRules.GetRule(uri) == FlashRuleMode.Legacy;

            if (alreadyLegacy)
            {
                UpdateManualLegacyButton();
                return;
            }

            // Basilisk pas dispo => rien (car activer legacy ne servirait à rien)
            if (!_legacyLauncher.CanLaunch())
            {
                content.FlashOverlay?.ShowBlocked("Flash Legacy indisponible : Basilisk n’est pas configuré.");
                UpdateManualLegacyButton();
                return;
            }

            // ✅ Popup custom
            var dlg = new controles.LegacyConfirmDialog();
            dlg.Owner = this;

            bool? ok = dlg.ShowDialog();
            if (ok != true)
                return;

            // ✅ Si Oui → on le met en Legacy direct (règle persistante)
            if (dlg.AddRule)
            {
                FlashDomainRules.SetRule(uri, FlashRuleMode.Legacy);

                // ✅ UI immédiate
                content.FlashMode = FlashMode.Legacy;

                ManualLegacyButton.IsEnabled = false;
                content.Web.CoreWebView2?.Reload();

                UpdateManualLegacyButton();
                return;
            }

            // ✅ Sinon : Legacy une fois (non persisté)
            content.ForceLegacyOnce = true;

            // ✅ UI immédiate (bouton orange dès le 1er clic)
            content.FlashMode = FlashMode.Legacy;

            ManualLegacyButton.IsEnabled = false;
            content.Web.CoreWebView2?.Reload();

            UpdateManualLegacyButton();
        }

        private void DownloadItem_Open(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.OpenFile(it);
        }
        private void DownloadItem_Remove(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Remove(it);
            UpdateDownloadsBadge();
        }

        private void DownloadItem_OpenFolder(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.OpenContainingFolder(it);
        }

        private void DownloadItem_Pause(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Pause(it);
        }

        private void DownloadItem_ResumeOrRetry(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Retry(it);
        }

        private void DownloadItem_Cancel(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Cancel(it);
        }

        bool HasAnyPrivateTab()
        {
            return Tabs.Items
                .OfType<TabItem>()
                .Any(t => t.Tag is WebTabContent w && w.IsPrivate);
        }

        private const int GW_OWNER = 4;

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        void FocusLegacyEmbedded(IntPtr embedHwnd, IntPtr topHwnd)
        {
            if (embedHwnd == IntPtr.Zero) return;

            uint thisThread = GetCurrentThreadId();
            uint targetThread = (uint)GetWindowThreadProcessId(embedHwnd, out _);

            // ✅ indispensable dans beaucoup de cas (threads différents)
            AttachThreadInput(thisThread, targetThread, true);
            try
            {
                // Top utile pour activation; focus sur embed pour clavier
                if (topHwnd != IntPtr.Zero)
                    SetActiveWindow(topHwnd);

                SetFocus(embedHwnd);
            }
            finally
            {
                AttachThreadInput(thisThread, targetThread, false);
            }
        }

        async Task<bool> LaunchLegacyIntoInternalTabAsync(WebTabContent content, Uri uri, BrowserTabHeader header)
        {
            if (content.IsLegacyLaunching)
                return false;

            content.IsLegacyLaunching = true;
            content.IsLegacyExternal = true;
            content.LegacyLastError = null;
            content.LegacyUrl = uri.AbsoluteUri;

            await Dispatcher.InvokeAsync(() =>
            {
                header.SetTitle("Legacy Flash");
                content.FlashMode = FlashMode.Legacy;
                SyncWebHostWithSelection();
            });

            await Task.Delay(50);

            if (!_legacyLauncher.CanLaunch())
            {
                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = "Basilisk n’est pas configuré ou chemin invalide.";

                await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                return false;
            }

            try
            {
                // Un nouvel essai ne doit pas laisser tourner l'instance précédente :
                // elle resterait orpheline et verrouillerait le profil Basilisk.
                await StopLegacyProcessAsync(content);
                content.LegacyHwnd = IntPtr.Zero;
                content.LegacyTopHwnd = IntPtr.Zero;
                content.LegacyEmbedHwnd = IntPtr.Zero;
                content.LegacyPid = null;

                content.LegacyProfileLease = LegacyProfileManager.CreateLease(uri.Host, content.IsPrivate);

                var p = _legacyLauncher.Launch(
                    uri.AbsoluteUri,
                    content.LegacyProfileLease.ProfilePath);
                content.LegacyProc = p;

                if (p == null)
                {
                    content.LegacyProfileLease?.Dispose();
                    content.LegacyProfileLease = null;
                    content.IsLegacyLaunching = false;
                    content.IsLegacyExternal = true;
                    content.LegacyLastError = "Process Basilisk non lancé (Launch() a retourné null).";

                    await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                    return false;
                }

                // Attendre la fenêtre principale de Basilisk.
                var (top, realPid) = await WaitForAnyTopWindowFromProcessFamilyAsync(p, timeoutMs: 15000, cancelled: () => content.IsClosed);

                // Onglet fermé pendant le lancement : on n'embarque rien.
                if (content.IsClosed)
                {
                    content.IsLegacyLaunching = false;
                    await StopLegacyProcessAsync(content);
                    return false;
                }

                if (top == IntPtr.Zero)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    content.LegacyProfileLease?.Dispose();
                    content.LegacyProfileLease = null;
                    content.IsLegacyLaunching = false;
                    content.IsLegacyExternal = true;
                    content.LegacyLastError = "Fenêtre Basilisk introuvable (timeout).";

                    await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                    return false;
                }

                // ✅ handles (ok hors UI)
                content.LegacyPid = realPid;
                content.LegacyTopHwnd = top;

                // ✅ enfant embed (focus/clavier)
                var embed = FindBestEmbedChild(top);
                var target = embed != IntPtr.Zero ? embed : top;

                content.LegacyHwnd = target;
                content.LegacyEmbedHwnd = target;

                // ✅ dock host : création OBLIGATOIRE sur UI thread STA
                if (content.LegacyHost == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        content.LegacyHost = new MyHomelabBrowser.controles.ExternalWindowDock();
                    });
                }

                // Tout ce qui touche WPF passe par le Dispatcher.
                await Dispatcher.InvokeAsync(() =>
                {
                    content.LegacyHost ??= new MyHomelabBrowser.controles.ExternalWindowDock();
                    content.LegacyHost.SetTopLevel(top);
                    content.LegacyHost.Bind(target);
                });

                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = null;

                await Dispatcher.InvokeAsync(() =>
                {
                    SyncWebHostWithSelection();

                    content.LegacyHost?.ShowDock();
                    content.LegacyHost?.UpdateDockPosition();
                }, DispatcherPriority.Loaded);

                return true;
            }
            catch (Exception ex)
            {
                try { content.LegacyProc?.Kill(entireProcessTree: true); } catch { }
                content.LegacyProc = null;
                content.LegacyProfileLease?.Dispose();
                content.LegacyProfileLease = null;
                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = "Erreur au lancement de Basilisk :\n" + ex;

                await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                return false;
            }
        }

        private void FlashDbg(string msg)
        {
            if (_settings.Settings.FlashDebugEnabled)
                MyHomelabBrowser.classes.Flash.FlashDebugConsole.Log(msg);
        }

        /// <summary>
        /// PID du processus lancé et de tous ses descendants (Basilisk relance souvent
        /// un processus enfant qui porte la vraie fenêtre). Un seul instantané système.
        /// </summary>
        private static HashSet<int> GetProcessFamilyPids(int rootPid)
        {
            var family = new HashSet<int> { rootPid };
            var childrenByParent = new Dictionary<int, List<int>>();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
                return family;

            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snapshot, ref pe))
                    return family;

                do
                {
                    int parent = (int)pe.th32ParentProcessID;
                    if (!childrenByParent.TryGetValue(parent, out var list))
                    {
                        list = new List<int>();
                        childrenByParent[parent] = list;
                    }
                    list.Add((int)pe.th32ProcessID);
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                }
                while (Process32Next(snapshot, ref pe));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                int parent = queue.Dequeue();
                if (!childrenByParent.TryGetValue(parent, out var children))
                    continue;

                foreach (int child in children)
                {
                    if (family.Add(child))
                        queue.Enqueue(child);
                }
            }

            return family;
        }

        /// <summary>
        /// Attend la première fenêtre visible de la famille de processus Basilisk.
        /// L'énumération Win32 tourne hors du thread UI pour ne pas figer l'interface.
        /// </summary>
        private static async Task<(IntPtr top, int realPid)> WaitForAnyTopWindowFromProcessFamilyAsync(
            Process rootProc,
            int timeoutMs = 15000,
            Func<bool>? cancelled = null)
        {
            int rootPid;
            try { rootPid = rootProc.Id; }
            catch { return (IntPtr.Zero, 0); }

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled?.Invoke() == true)
                    break;

                var found = await Task.Run(() =>
                {
                    foreach (var pid in GetProcessFamilyPids(rootPid))
                    {
                        var hwnd = FindAnyTopLevelWindowForPid(pid);
                        if (hwnd != IntPtr.Zero)
                            return (hwnd, pid);
                    }

                    return (IntPtr.Zero, 0);
                });

                if (found.Item1 != IntPtr.Zero)
                    return found;

                await Task.Delay(120);
            }

            return (IntPtr.Zero, 0);
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static IntPtr FindAnyTopLevelWindowForPid(int pid)
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out int winPid);
                if (winPid != pid)
                    return true;

                if (!IsWindowVisible(hWnd))
                    return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        // ===============================
        // CONTENU D’ONGLET (BASE PROPRE)
        // ===============================
        public abstract class TabContent { }

       public class WebTabContent : TabContent
        {
            public WebView2 Web { get; init; } = null!;
            public bool IsPinned { get; set; }
            public bool IsSuspended { get; set; }
            public bool IsPrivate { get; set; }
            public DateTime LastActivated { get; set; } = DateTime.Now;

            // FLASH
            public FlashDecisionService? FlashService { get; set; }
            public FlashMode FlashMode { get; set; } = FlashMode.None;
            public bool FlashRequired { get; set; }
            public bool? LastFlashButtonVisible { get; set; }
            public FlashMode? LastFlashMode { get; set; }
            public Grid HostGrid { get; set; } = new Grid();
            public bool IsLegacyLaunching { get; set; }
            public string? LegacyLastError { get; set; }
            public LegacyFlashView? LegacyView { get; set; }
            public IntPtr LegacyHwnd { get; set; } = IntPtr.Zero;
            public ExternalWindowDock? LegacyHost;
            public bool IsCustomView { get; set; }
            public Func<UserControl>? CreateView { get; set; }

            public Process? LegacyProc { get; set; }
            public LegacyProfileLease? LegacyProfileLease { get; set; }
            public IntPtr LegacyTopHwnd { get; set; } = IntPtr.Zero;
            public bool ForceLegacyOnce { get; set; } = false;
            public bool FlashChecked { get; set; } = false;
            public IntPtr LegacyEmbedHwnd { get; set; } = IntPtr.Zero;

            // Legacy proxy
            public bool IsLegacyExternal { get; set; }
            public string? LegacyUrl { get; set; }
            public int? LegacyPid { get; set; }

            public RuffleMonitor? RuffleMonitor { get; set; }
            public CancellationTokenSource? FlashNavigationCts { get; set; }
            public int FlashNavigationGeneration { get; set; }
            public bool FlashNetworkHookAttached { get; set; }
            public FlashDetectionResult LastFlashDetection { get; set; } = FlashDetectionResult.None;
            public string? RuffleFailureReason { get; set; }
            public RuffleStatus? RuffleFailureStatus { get; set; }

            public FlashUxOverlay? FlashOverlay { get; set; }

            // Cycle de vie de l'onglet
            public TabItem? OwnerTab { get; set; }
            public bool IsClosed { get; set; }
            public bool IsShutDown { get; set; }
            public bool IsLoading { get; set; }
            public string? PendingUrl { get; set; }
            public HistoryEntry? LastHistoryEntry { get; set; }
            public DateTime LastPopupNoticeUtc { get; set; } = DateTime.MinValue;
        }
        class ViewTabContent : TabContent
        {
            public UserControl View { get; init; } = null!;
        }

    }
}