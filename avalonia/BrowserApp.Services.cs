using System;
using System.IO;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Workspaces;
using PommeBrowser.Core;
using PommeBrowser.Legacy;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser
{
    /// <summary>Services du homelab, coffre, espaces de travail et Basilisk.</summary>
    public sealed partial class BrowserApp
    {
        ServiceMonitor? _monitor;

        public HomelabServiceStore Services { get; } = new(() => AppPaths.Profile("services.json"));

        public Vault Vault { get; }

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

        /// <summary>Basilisk peut se lancer (réglage « Utiliser Basilisk ») : faux, il ne se lance jamais.</summary>
        public bool BasiliskAllowed => Settings.BasiliskEnabled != false;

        /// <summary>
        /// Basilisk choisi dans les paramètres, sinon celui livré avec PommeBrowser, sinon (Linux)
        /// celui trouvé à un emplacement habituel. Null : aucun.
        /// </summary>
        public string? BasiliskExecutable
        {
            get
            {
                string? chosen = string.IsNullOrWhiteSpace(Settings.BasiliskPath) ? null : Settings.BasiliskPath;
                if (OperatingSystem.IsLinux())
                {
                    if (BasiliskInstall.IsLaunchable(chosen))
                        return chosen;
                    return BasiliskInstall.IsLaunchable(LegacyEngine.BundledExecutable)
                        ? LegacyEngine.BundledExecutable
                        : BasiliskInstall.Detect(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("PATH"));
                }
                if (!OperatingSystem.IsWindows())
                    return null;
                if (BasiliskExecutable_IsLaunchable(chosen))
                    return chosen;
                return BasiliskExecutable_IsLaunchable(LegacyEngine.BundledExecutable) ? LegacyEngine.BundledExecutable : null;
            }
        }

        static bool BasiliskExecutable_IsLaunchable(string? path) => MyHomelabBrowser.BasiliskExecutable.IsLaunchable(path);
    }
}
