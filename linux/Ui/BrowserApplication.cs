using System;
using System.Collections.Generic;
using System.Linq;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Linux.Core;
using PommeBrowser.Linux.Web;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Application : données du profil, moteur web et fenêtres. Une seule instance tourne ;
    /// une adresse ouverte depuis un autre programme arrive ici dans un nouvel onglet.
    /// </summary>
    sealed class BrowserApplication
    {
        readonly List<BrowserWindow> _windows = new();
        readonly string _settingsPath = LinuxPaths.Profile("settings-linux.json");
        bool _quitting;

        public BrowserApplication(AppearanceSettings appearance, ProfileService profiles)
        {
            Appearance = appearance;
            Profiles = profiles;
            Settings = LinuxSettings.Load(_settingsPath);

            App = Adw.Application.New(LinuxPaths.AppId, Gio.ApplicationFlags.HandlesOpen);
            App.OnStartup += (_, _) => Startup();
            App.OnActivate += (_, _) => Activate();
            App.OnOpen += (_, args) => Open(args.Files.Select(f => f.GetUri()).ToArray());
            App.OnShutdown += (_, _) => Shutdown();
        }

        public Adw.Application App { get; }
        public AppearanceSettings Appearance { get; }
        public ProfileService Profiles { get; }
        public Vault Vault { get; } = new();
        public LinuxSettings Settings { get; private set; }

        public WebEngine Engine { get; private set; } = null!;
        public FavoritesStore Favorites { get; private set; } = null!;
        public HistoryService History { get; private set; } = null!;
        public HomelabServiceStore Services { get; } = new(() => LinuxPaths.Profile("services.json"));
        public ServiceMonitor Monitor { get; private set; } = null!;
        public SiteSecurityStore SiteSecurity => SiteSecurityStore.Current;
        public CertificatePinStore CertificatePins { get; } = new(() => LinuxPaths.Profile("pinned-certificates.json"));
        public SiteZoomStore Zoom { get; } = new(() => LinuxPaths.Profile("zoom.json"));

        /// <summary>Hôtes dont le certificat a été accepté pendant cette session (non épinglé).</summary>
        public HashSet<string> SessionTrustedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>État connu de chaque service (mis à jour par la surveillance).</summary>
        public event Action? ServicesChanged;

        public IReadOnlyList<BrowserWindow> Windows => _windows;

        public BrowserWindow? ActiveWindow
            => App.GetActiveWindow() is { } active ? _windows.FirstOrDefault(w => w.Window.Handle.DangerousGetHandle() == active.Handle.DangerousGetHandle()) : _windows.LastOrDefault();

        /// <summary>GApplication attend le nom du programme en premier, comme argv en C (sinon la première adresse est ignorée).</summary>
        public int Run(string[] args) => App.RunWithSynchronizationContext(args.Prepend("pommebrowser").ToArray());

        void Startup()
        {
            MainThread.Initialize();
            Styles.Load();
            Gtk.Window.SetDefaultIconName(LinuxPaths.AppId);
            ApplyTheme();

            Favorites = new FavoritesStore(LinuxPaths.Profile("favorites.json"));
            History = new HistoryService(LinuxPaths.Data("history.db"));
            Engine = new WebEngine(() => Settings);

            Monitor = new ServiceMonitor(() => Services.GetAll());
            Monitor.Checked += (_, _) => MainThread.Post(() => ServicesChanged?.Invoke());
            Monitor.StateChanged += (service, _, now) => MainThread.Post(() => NotifyServiceState(service, now));
            Services.Changed += () => MainThread.Post(() =>
            {
                ServicesChanged?.Invoke();
                _ = Monitor.CheckAllAsync();
            });
            ApplyMonitoring();

            Engine.Downloads.Completed += entry =>
            {
                if (entry.State == DownloadState.Finished)
                    ActiveWindow?.ShowDownloadFinished(entry);
            };

            AddActions();
            _ = Engine.AdBlocker.StartAsync();
        }

        void Activate()
        {
            if (_windows.Count > 0)
            {
                ActiveWindow?.Window.Present();
                return;
            }

            BrowserWindow window = CreateWindow(isPrivate: false);
            SessionState session = Settings.RestoreSession ? SessionStore.Load(LinuxPaths.Data("session.json")) : new SessionState();
            if (session.Tabs.Count > 0)
                window.RestoreSession(session);
            else
                window.OpenHomeTab();
            window.Window.Present();
        }

        void Open(string[] uris)
        {
            BrowserWindow window = _windows.LastOrDefault(w => !w.IsPrivate) ?? CreateWindow(isPrivate: false);
            foreach (string uri in uris.Where(u => !string.IsNullOrWhiteSpace(u)))
                window.OpenInNewTab(uri, background: false);
            if (window.TabCount == 0)
                window.OpenHomeTab();
            window.Window.Present();
        }

        public BrowserWindow CreateWindow(bool isPrivate)
        {
            var window = new BrowserWindow(this, isPrivate);
            _windows.Add(window);
            window.Window.OnCloseRequest += (_, _) =>
            {
                if (!window.IsPrivate && !_quitting && _windows.Count(w => !w.IsPrivate) == 1)
                    SaveSession(new[] { window });
                return false;
            };
            window.Window.OnDestroy += (_, _) => _windows.Remove(window);
            return window;
        }

        public void Quit() => Quit(saveSession: true);

        void Quit(bool saveSession)
        {
            _quitting = true;
            if (saveSession)
                SaveSession(_windows.Where(w => !w.IsPrivate).ToList());
            foreach (BrowserWindow window in _windows.ToList())
                window.Window.Destroy();
            App.Quit();
        }

        /// <summary>
        /// Change de profil (connexion, création, renommage, suppression) puis relance PommeBrowser.
        /// La session du profil quitté est enregistrée avant le changement. Si <paramref name="change"/>
        /// échoue, l'exception remonte à l'appelant et rien n'est relancé ; sinon la relance a lieu
        /// juste après, une fois la boîte de dialogue refermée.
        /// </summary>
        public void ChangeProfile(Action change)
        {
            SaveSession(_windows.Where(w => !w.IsPrivate).ToList());
            History.Flush();
            change();
            MainThread.Post(() => Restart(saveSession: false));
        }

        /// <summary>Relance PommeBrowser (nouveau profil, mise à jour installée).</summary>
        public void Restart(bool saveSession = true)
        {
            try
            {
                AppRestart.Schedule();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
            {
                // PommeBrowser se ferme quand même : le prochain lancement ouvrira le bon profil.
                RuntimeLogBuffer.Append("[Relance] " + ex.Message);
            }
            Quit(saveSession);
        }

        void SaveSession(IReadOnlyCollection<BrowserWindow> windows)
        {
            var state = new SessionState();
            foreach (BrowserWindow window in windows)
            {
                if (window == ActiveWindow || windows.Count == 1)
                    state.Selected = state.Tabs.Count + window.SelectedIndex;
                state.Tabs.AddRange(window.GetSessionTabs());
            }
            SessionStore.Save(LinuxPaths.Data("session.json"), state);
        }

        void Shutdown()
        {
            History?.Flush();
            History?.Dispose();
            Monitor?.Dispose();
            Engine?.Dispose();
        }

        public void SaveSettings()
        {
            Settings.Save(_settingsPath);
            Engine.ApplyPrivacySettings();
            ApplyMonitoring();
            foreach (BrowserWindow window in _windows)
                window.OnSettingsChanged();
        }

        public void SaveAppearance()
        {
            try
            {
                Appearance.Save();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Apparence] " + ex.Message);
            }
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            Adw.StyleManager.GetDefault().SetColorScheme(Appearance.Theme switch
            {
                AppTheme.Dark => Adw.ColorScheme.ForceDark,
                AppTheme.Light => Adw.ColorScheme.ForceLight,
                _ => Adw.ColorScheme.Default
            });
        }

        void ApplyMonitoring()
        {
            if (Settings.ServiceMonitoring)
            {
                Monitor.Stop();
                Monitor.Start(TimeSpan.FromSeconds(Settings.ServiceCheckIntervalSeconds));
            }
            else
            {
                Monitor.Stop();
            }
        }

        void NotifyServiceState(HomelabService service, ServiceState now)
        {
            ServicesChanged?.Invoke();
            if (!Settings.ServiceAlerts)
                return;

            bool up = now is ServiceState.Online or ServiceState.Degraded;
            var notification = Gio.Notification.New(up
                ? Tr("{0} est de nouveau en ligne", service.Name)
                : Tr("{0} ne répond plus", service.Name));
            notification.SetBody(service.Url);
            notification.SetPriority(up ? Gio.NotificationPriority.Normal : Gio.NotificationPriority.High);
            notification.SetDefaultActionAndTarget("app.open-url", GLib.Variant.NewString(service.Url));
            App.SendNotification("service-" + service.Id, notification);
        }

        void AddActions()
        {
            AddAction("new-window", () => { BrowserWindow w = CreateWindow(false); w.OpenHomeTab(); w.Window.Present(); }, "<Control>n");
            AddAction("new-private-window", () => { BrowserWindow w = CreateWindow(true); w.OpenHomeTab(); w.Window.Present(); }, "<Control><Shift>n", "<Control><Shift>p");
            AddAction("preferences", () => PreferencesWindow.Show(this, ActiveWindow), "<Control>comma");
            AddAction("about", ShowAbout);
            AddAction("quit", Quit, "<Control>q");

            var openUrl = Gio.SimpleAction.New("open-url", GLib.VariantType.New("s"));
            openUrl.OnActivate += (_, args) =>
            {
                string? url = args.Parameter?.GetString(out nuint _);
                if (!string.IsNullOrWhiteSpace(url))
                    Open(new[] { url });
            };
            App.AddAction(openUrl);
        }

        void AddAction(string name, Action handler, params string[] accelerators)
        {
            var action = Gio.SimpleAction.New(name, null);
            action.OnActivate += (_, _) => handler();
            App.AddAction(action);
            if (accelerators.Length > 0)
                App.SetAccelsForAction("app." + name, accelerators);
        }

        void ShowAbout()
        {
            var about = Adw.AboutDialog.New();
            about.SetApplicationName("PommeBrowser");
            about.SetApplicationIcon(LinuxPaths.AppId);
            about.SetVersion(typeof(BrowserApplication).Assembly.GetName().Version?.ToString(3) ?? "");
            about.SetDeveloperName("vazer7070");
            about.SetWebsite("https://github.com/vazer7070/MyHomelabBrowser");
            about.SetIssueUrl("https://github.com/vazer7070/MyHomelabBrowser/issues");
            about.SetComments(Tr("Navigateur pour le homelab : services surveillés, blocage des publicités, contenus Flash avec Ruffle."));
            about.SetDebugInfo(DebugInfo());
            about.Present(ActiveWindow?.Window);
        }

        string DebugInfo()
            => $"PommeBrowser {typeof(BrowserApplication).Assembly.GetName().Version}\n" +
               $"WebKitGTK {WebKit.Functions.GetMajorVersion()}.{WebKit.Functions.GetMinorVersion()}.{WebKit.Functions.GetMicroVersion()}\n" +
               $"GTK {Gtk.Functions.GetMajorVersion()}.{Gtk.Functions.GetMinorVersion()}.{Gtk.Functions.GetMicroVersion()}\n" +
               $"libadwaita {Adw.Functions.GetMajorVersion()}.{Adw.Functions.GetMinorVersion()}.{Adw.Functions.GetMicroVersion()}\n" +
               $"Ruffle {Engine.Ruffle.InstalledVersion ?? "-"}\n" +
               $".NET {Environment.Version}\n\n" + RuntimeLogBuffer.GetSnapshot();
    }
}
