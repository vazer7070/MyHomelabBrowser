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

        public Task<UpdateInfo?> CheckAsync()
            => _mgr.CheckForUpdatesAsync();

        public Task DownloadAsync(UpdateInfo info)
            => _mgr.DownloadUpdatesAsync(info);

        public void ApplyAndRestart(UpdateInfo info)
            => _mgr.ApplyUpdatesAndRestart(info);
    }
}