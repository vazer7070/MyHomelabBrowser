using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace MyHomelabBrowser.classes.Updates
{
    public class UpdateService
    {
        private readonly UpdateManager _mgr;

        public UpdateService(string feedUrl)
        {
            // feedUrl = ".../releases/latest/download/"
            _mgr = new UpdateManager(new GithubSource(feedUrl, "", false));
        }

        public Task<UpdateInfo?> CheckAsync()
            => _mgr.CheckForUpdatesAsync();

        public Task DownloadAsync(UpdateInfo info)
            => _mgr.DownloadUpdatesAsync(info);

        public void ApplyAndRestart(UpdateInfo info)
            => _mgr.ApplyUpdatesAndRestart(info);

        public async Task<bool> CheckAndApplySilentAsync()
        {
            var info = await CheckAsync();
            if (info == null)
                return false;

            await DownloadAsync(info);
            ApplyAndRestart(info);
            return true;
        }
    }
}