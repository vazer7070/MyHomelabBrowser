using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace MyHomelabBrowser.classes
{
    public class UpdateService
    {
        private readonly UpdateManager _mgr;

        public UpdateService()
        {
            _mgr = new UpdateManager(
                new GithubSource(
                    "https://github.com/vazer7070/PommeBrowser-release",
                    null,
                    prerelease: false
                )
            );
        }

        /// <summary>
        /// Faux pour une exécution depuis Visual Studio ou un dossier non installé par
        /// Velopack : la vérification échouerait systématiquement.
        /// </summary>
        public bool IsInstalled => _mgr.IsInstalled;

        public Task<UpdateInfo?> CheckAsync()
            => _mgr.CheckForUpdatesAsync();

        public Task DownloadAsync(UpdateInfo info, Action<int>? progress = null, CancellationToken cancellationToken = default)
            => _mgr.DownloadUpdatesAsync(info, progress, cancellationToken);

        public void ApplyAndRestart(UpdateInfo info)
            => _mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);

        /// <summary>
        /// Programme l'installation d'une mise à jour déjà téléchargée pour le moment
        /// où l'utilisateur fermera le navigateur, sans relancer l'application.
        /// </summary>
        public void ApplyOnExit(UpdateInfo info)
            => _mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
    }
}
