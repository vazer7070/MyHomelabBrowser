using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Localization;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Core;
using PommeBrowser.Engine;
using PommeBrowser.Legacy;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views;

namespace PommeBrowser
{
    /// <summary>Onglet fermé, rouvrable avec Ctrl+Maj+T.</summary>
    public sealed record ClosedTab(string Url, string? Title, bool IsPrivate, MainWindow? Window, int Index);

    /// <summary>
    /// Application : données du profil ouvert, réglages, moteur web et fenêtres. Une seule
    /// instance par profil ; les fenêtres partagent tout, sauf la navigation privée.
    /// </summary>
    public sealed partial class BrowserApp
    {
        const int MaxClosedTabs = 25;

        readonly List<MainWindow> _windows = new();
        readonly List<ClosedTab> _closedTabs = new();
        IClassicDesktopStyleApplicationLifetime _lifetime = null!;

        public BrowserApp(ProfileService profiles, AppearanceSettings appearance)
        {
            Current = this;
            Profiles = profiles;
            Appearance = appearance;
            SettingsService = new SettingsService();
            SettingsMigration.ImportLinuxSettings(SettingsService);
            SettingsMigration.AdaptToPlatform(SettingsService);
            Favorites = new FavoritesStore(AppPaths.FavoritesFile);
            History = new HistoryService(AppPaths.HistoryDatabase);
            Zoom = new SiteZoomStore(() => AppPaths.Profile("zoom.json"));
            CertificatePins = new CertificatePinStore(() => AppPaths.Profile("pinned-certificates.json"));
            Vault = new Vault(() => Settings.VaultAutoLockMinutes);
        }

        public static BrowserApp Current { get; private set; } = null!;

        public ProfileService Profiles { get; }
        public AppearanceSettings Appearance { get; }
        public SettingsService SettingsService { get; }
        public BrowserSettings Settings => SettingsService.Settings;
        public FavoritesStore Favorites { get; }
        public HistoryService History { get; }
        public SiteZoomStore Zoom { get; }
        public CertificatePinStore CertificatePins { get; }
        public SiteSecurityStore SiteSecurity => SiteSecurityStore.Current;
        public DownloadList Downloads { get; } = new();

        /// <summary>Hôtes dont le certificat a été accepté pendant cette session (non épinglé).</summary>
        public HashSet<string> SessionTrustedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hôtes ramenés sur Ruffle après une bascule automatique : plus de bascule pendant la session.</summary>
        public HashSet<string> SessionRuffleHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hôtes qui ne répondent pas en HTTPS (pas de nouvel essai pendant la session).</summary>
        public HashSet<string> HttpOnlyHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<MainWindow> Windows => _windows;

        public MainWindow? ActiveWindow => _windows.FirstOrDefault(w => w.IsActive) ?? _windows.LastOrDefault();

        public IReadOnlyList<ClosedTab> ClosedTabs => _closedTabs;

        /// <summary>
        /// Dossier des téléchargements : celui choisi dans les paramètres, sinon celui du système
        /// (sous Linux, le dossier XDG, souvent traduit : ~/Téléchargements).
        /// </summary>
        public string DownloadDirectory
        {
            get
            {
                string? chosen = Settings.DownloadFolder;
                if (string.IsNullOrWhiteSpace(chosen) || !Path.IsPathRooted(chosen))
                    return AppPaths.DefaultDownloadDirectory;
                string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return !Directory.Exists(chosen) && string.Equals(chosen, defaultFolder, StringComparison.Ordinal)
                    ? AppPaths.DefaultDownloadDirectory
                    : chosen;
            }
        }

        /// <summary>Page des nouveaux onglets (null : page d'accueil de PommeBrowser).</summary>
        public string? NewTabUrl
        {
            get
            {
                string url = Settings.NewTabPage?.Trim() ?? string.Empty;
                return url.Length == 0 || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : UrlResolver.ResolveOrSearch(url, Settings.Search);
            }
        }

        /// <summary>
        /// Démarrage (ou profil ouvert sans relance) : fenêtre et onglets de la session.
        /// <paramref name="placement"/> : position et taille de la fenêtre du profil quitté.
        /// </summary>
        public void Start(IClassicDesktopStyleApplicationLifetime lifetime, IReadOnlyList<string> urls, WindowPlacement? placement = null)
        {
            _lifetime = lifetime;
            Dispatcher.UIThread.UnhandledException += OnUnhandledException;
            lifetime.ShutdownMode = ShutdownMode.OnLastWindowClose;
            lifetime.ShutdownRequested += OnShutdownRequested;

            ApplyTheme();
            ConfigureEngine();
            SettingsService.SettingsChanged += _ => ConfigureEngine();
            StartServices();
            ScheduleUpdateCheck();

            MainWindow window = OpenWindow();
            placement?.ApplyTo(window);
            RestoreSession(window, urls);
            window.Show();

            // Durée du démarrage, jusqu'à la fenêtre affichée (journal joint aux rapports).
            if (placement == null)
            {
                Dispatcher.UIThread.Post(() => RuntimeLogBuffer.Append(
                    $"[Démarrage] fenêtre affichée en {System.Diagnostics.Stopwatch.GetElapsedTime(Program.StartedAt).TotalMilliseconds:F0} ms"),
                    DispatcherPriority.Background);
            }
        }

        void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
        {
            if (!_quitting)
                SaveSession();
            LegacyBrowser.CloseAll();
        }

        DateTime _lastErrorToast = DateTime.MinValue;

        /// <summary>
        /// Filet de sécurité du fil de l'interface : une erreur imprévue est consignée (errors.log)
        /// au lieu d'arrêter PommeBrowser avec tous ses onglets. Échec du démarrage du moteur web
        /// (relancé par Avalonia) : les onglets qui l'attendaient l'affichent ; sinon, une notification.
        /// </summary>
        void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            Exception exception = e.Exception;
            ErrorLog.Write("Interface", exception);
            try
            {
                bool shown = false;
                if (EngineHost.IsWebViewFailure(exception))
                {
                    foreach (MainWindow window in _windows.ToList())
                    {
                        foreach (BrowserTab tab in window.Tabs.ToList())
                            shown |= tab.OnEngineStartupFailed(exception);
                    }
                }

                // Au plus une notification toutes les cinq secondes (erreur répétée en boucle).
                if (!shown && DateTime.UtcNow - _lastErrorToast > TimeSpan.FromSeconds(5))
                {
                    _lastErrorToast = DateTime.UtcNow;
                    ActiveWindow?.ShowToast(Loc.Tr("Erreur inattendue : {0}", exception.Message), warning: true);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ErrorLog.Write("Interface (affichage de l'erreur)", ex);
            }
        }

        /// <summary>Thème choisi (système, sombre ou clair).</summary>
        public void ApplyTheme()
        {
            if (Application.Current is { } application)
            {
                application.RequestedThemeVariant = Appearance.Theme switch
                {
                    AppTheme.Dark => ThemeVariant.Dark,
                    AppTheme.Light => ThemeVariant.Light,
                    _ => ThemeVariant.Default
                };
            }
            // Les sites qui proposent un thème sombre suivent celui du navigateur.
            if (_lifetime != null)
                ConfigureEngine();
        }

        /// <summary>Réglages transmis au moteur (confidentialité, langues, téléchargements).</summary>
        public void ConfigureEngine()
        {
            bool english = Loc.Language == "en";
            EngineHost.Configure(new EngineSettings
            {
                Languages = english ? new[] { "en-US", "en", "fr-FR", "fr" } : new[] { "fr-FR", "fr", "en-US", "en" },
                SpellCheckingLanguages = english ? new[] { "en_US" } : new[] { "fr_FR" },
                TrackingPrevention = Settings.TrackingPrevention != BrowserSettings.TrackingProtection.Off,
                BlockThirdPartyCookies = Settings.TrackingPrevention >= BrowserSettings.TrackingProtection.Balanced,
                TrackingLevel = Settings.TrackingPrevention,
                DarkPages = Appearance.Theme switch
                {
                    AppTheme.Dark => true,
                    AppTheme.Light => false,
                    _ => null
                },
                BrowserArguments = SecureDnsConfiguration.BuildAdditionalBrowserArguments(Settings, string.Empty),
                DownloadDirectory = DownloadDirectory,
                CookieDatabase = AppPaths.CookieDatabase,
                WebView2UserDataFolder = OperatingSystem.IsWindows() ? AppPaths.WebView2UserDataFolder : null,
                AppleDataStoreId = OperatingSystem.IsMacOS() ? AppPaths.AppleDataStoreId : null
            });
        }

        public MainWindow OpenWindow()
        {
            var window = new MainWindow(this);
            _windows.Add(window);
            window.Closed += (_, _) =>
            {
                _windows.Remove(window);
                _closedTabs.RemoveAll(t => t.Window == window);
            };
            return window;
        }

        /// <summary>Nouvelle fenêtre, avec la page d'accueil ou l'adresse donnée.</summary>
        public MainWindow NewWindow(string? url = null, bool isPrivate = false)
        {
            MainWindow window = OpenWindow();
            window.NewTab(url, select: true, isPrivate: isPrivate);
            window.Show();
            return window;
        }

        public void RememberClosedTab(ClosedTab tab)
        {
            if (tab.IsPrivate || !SessionStore.IsRestorable(tab.Url))
                return;
            _closedTabs.Add(tab);
            if (_closedTabs.Count > MaxClosedTabs)
                _closedTabs.RemoveAt(0);
        }

        public ClosedTab? TakeClosedTab(MainWindow window)
        {
            int index = _closedTabs.FindLastIndex(t => t.Window == window || t.Window == null || !_windows.Contains(t.Window));
            if (index < 0)
                return null;
            ClosedTab tab = _closedTabs[index];
            _closedTabs.RemoveAt(index);
            return tab;
        }

        /// <summary>Ferme toutes les fenêtres (la session est gardée).</summary>
        public void Quit()
        {
            SaveSession();
            Shutdown();
        }

        /// <summary>Exécute sur le fil de l'interface.</summary>
        public static void Post(Action action) => Dispatcher.UIThread.Post(action);
    }
}
