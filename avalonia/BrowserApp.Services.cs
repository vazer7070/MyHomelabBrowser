using System;
using System.IO;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Workspaces;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser
{
    /// <summary>Services du homelab, coffre, espaces de travail et Basilisk.</summary>
    public sealed partial class BrowserApp
    {
        ServiceMonitor? _monitor;

        public HomelabServiceStore Services { get; } = new(() => AppPaths.Profile("services.json"));

        public Vault Vault { get; } = new();

        public AdBlockService AdBlock { get; } = new();

        public WorkspaceStore Workspaces { get; } = new(() => AppPaths.Profile("workspaces.json"));

        /// <summary>Surveillance de la disponibilité des services (résultats et changements d'état).</summary>
        public ServiceMonitor Monitor => _monitor ??= CreateMonitor();

        /// <summary>État d'un service vérifié ou modifié (fil de l'interface).</summary>
        public event Action? ServicesChanged;

        ServiceMonitor CreateMonitor()
        {
            var monitor = new ServiceMonitor(() => Services.GetAll());
            monitor.Checked += (_, _) => Post(() => ServicesChanged?.Invoke());
            monitor.StateChanged += (service, _, now) => Post(() => NotifyServiceState(service, now));
            Services.Changed += () => Post(() =>
            {
                ServicesChanged?.Invoke();
                _ = monitor.CheckAllAsync();
            });
            return monitor;
        }

        void StartServices()
        {
            // Linux (comme l'édition GTK) : profils Basilisk avec les données du profil, ils contiennent un cache.
            if (OperatingSystem.IsLinux())
                LegacyProfileManager.RootOverride = AppPaths.Data("basilisk");
            ApplyMonitoring();
            SettingsService.SettingsChanged += _ =>
            {
                ApplyMonitoring();
                foreach (Views.MainWindow window in _windows)
                {
                    foreach (Views.BrowserTab tab in window.Tabs)
                        tab.OnSettingsChanged();
                }
            };
            _ = AdBlock.StartAsync();
        }

        void ApplyMonitoring()
        {
            Monitor.Stop();
            if (Settings.ServiceMonitoring)
                Monitor.Start(TimeSpan.FromSeconds(Math.Clamp(Settings.ServiceCheckIntervalSeconds, 15, 3600)));
        }

        void NotifyServiceState(HomelabService service, ServiceState now)
        {
            ServicesChanged?.Invoke();
            if (!Settings.ServiceAlerts)
                return;

            bool up = now is ServiceState.Online or ServiceState.Degraded;
            ActiveWindow?.ShowToast(up ? Tr("{0} est de nouveau en ligne", service.Name) : Tr("{0} ne répond plus", service.Name),
                Tr("Ouvrir"), () => ActiveWindow?.NewTab(service.Url, select: true), warning: !up);
        }

        /// <summary>Basilisk choisi dans les paramètres, sinon trouvé à un emplacement habituel (null : absent).</summary>
        public string? BasiliskExecutable
        {
            get
            {
                string? chosen = string.IsNullOrWhiteSpace(Settings.BasiliskPath) ? null : Settings.BasiliskPath;
                if (OperatingSystem.IsLinux())
                {
                    return BasiliskInstall.IsLaunchable(chosen)
                        ? chosen
                        : BasiliskInstall.Detect(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("PATH"));
                }
                return OperatingSystem.IsWindows() && BasiliskExecutable_IsLaunchable(chosen) ? chosen : null;
            }
        }

        static bool BasiliskExecutable_IsLaunchable(string? path) => MyHomelabBrowser.BasiliskExecutable.IsLaunchable(path);
    }
}
