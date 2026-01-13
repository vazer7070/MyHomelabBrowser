using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class RuffleMonitor
    {
        private readonly WpfWebView2 _webView;
        private readonly DispatcherTimer _timer;

        private readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _freezeTimeout = TimeSpan.FromSeconds(6);

        private DateTime _startedAt;

        public event Action<string>? FailureDetected;

        public RuffleMonitor(WpfWebView2 webView)
        {
            _webView = webView;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => await TickAsync();
        }

        public void Start()
        {
            _startedAt = DateTime.UtcNow;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        private async Task TickAsync()
        {
            if (_webView.CoreWebView2 == null)
                return;

            var st = await RuffleInjector.GetStatusAsync(_webView);

            if (!st.Exists)
            {
                if (DateTime.UtcNow - _startedAt > _startTimeout)
                {
                    FailureDetected?.Invoke("Ruffle status absent (timeout)");
                    Stop();
                }
                return;
            }

            if (!st.Started && DateTime.UtcNow - _startedAt > _startTimeout)
            {
                FailureDetected?.Invoke("Ruffle n'a pas démarré (timeout)");
                Stop();
                return;
            }

            if (st.LastFrameAt > 0)
            {
                var lastFrame = DateTimeOffset.FromUnixTimeMilliseconds(st.LastFrameAt).UtcDateTime;
                if (DateTime.UtcNow - lastFrame > _freezeTimeout)
                {
                    FailureDetected?.Invoke("Ruffle freeze détecté");
                    Stop();
                    return;
                }
            }

            // erreurs significatives
            foreach (var e in st.Errors)
            {
                if (e.Contains("ruffle-script-not-loaded", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("panic", StringComparison.OrdinalIgnoreCase) ||
                    e.Contains("load-failed", StringComparison.OrdinalIgnoreCase))
                {
                    FailureDetected?.Invoke("Erreur Ruffle: " + e);
                    Stop();
                    return;
                }
            }
        }
    }
}
