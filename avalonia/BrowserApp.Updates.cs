using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using PommeBrowser.Updates;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser
{
    /// <summary>Mises à jour : AppImage sous Linux, Velopack sous Windows.</summary>
    public sealed partial class BrowserApp
    {
        IUpdater? _updater;

        public IUpdater Updater => _updater ??=
            OperatingSystem.IsLinux() ? new AppImageUpdater() :
            OperatingSystem.IsWindows() ? new VelopackUpdater() :
            new NoUpdater();

        /// <summary>Recherche peu après le démarrage, pour ne pas ralentir l'ouverture des pages.</summary>
        void ScheduleUpdateCheck()
        {
            if (Settings.AutoUpdate && Updater.CanUpdate)
                DispatcherTimer.RunOnce(() => _ = CheckForUpdatesAsync(manual: false), TimeSpan.FromSeconds(20));
        }

        /// <summary>Recherche (et installe si possible) une nouvelle version, puis propose de redémarrer.</summary>
        public async Task CheckForUpdatesAsync(bool manual)
        {
            await Updater.CheckAsync();
            if (Updater.Installed is { } installed)
            {
                ActiveWindow?.ShowToast(Tr("PommeBrowser {0} est installé.", installed.ToString(3)), Tr("Redémarrer"), RestartToUpdate, timeout: 0);
            }
            else if (!manual && OperatingSystem.IsLinux() && Updater is AppImageUpdater { Available: not null } && Updater.ReleasePage is { } page)
            {
                ActiveWindow?.ShowToast(Updater.Status, Tr("Voir"), () => ActiveWindow?.NewTab(page, select: true));
            }
        }

        /// <summary>Redémarre sur la version installée, en rouvrant les onglets.</summary>
        public void RestartToUpdate()
        {
            if (OperatingSystem.IsWindows() && Updater is VelopackUpdater velopack)
            {
                SaveSession(isUpdateRestart: true);
                _quitting = true;
                if (velopack.ApplyAndRestart())
                    return;
            }
            Restart(saveSession: true, isUpdateRestart: true);
        }
    }
}
