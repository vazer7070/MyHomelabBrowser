using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class RuffleMonitor : IDisposable
    {
        private readonly WpfWebView2 _webView;
        private readonly DispatcherTimer _timer;
        private readonly SemaphoreSlim _tickLock = new(1, 1);
        private readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(30);
        private readonly TimeSpan _stabilityWindow = TimeSpan.FromSeconds(45);
        private DateTime _startedAtUtc;
        private DateTime? _readyAtUtc;
        private CancellationTokenSource? _cts;
        private bool _failureRaised;
        private bool _readyRaised;
        private bool _disposed;
        private int _missingAfterReadyTicks;

        public event Action<RuffleFailureInfo>? FailureDetected;
        public event Action<RuffleStatus>? ReadyDetected;

        public RuffleStatus LastStatus { get; private set; } = new();

        public RuffleMonitor(WpfWebView2 webView)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            ThrowIfDisposed();
            Stop();
            _failureRaised = false;
            _readyRaised = false;
            _missingAfterReadyTicks = 0;
            _readyAtUtc = null;
            LastStatus = new RuffleStatus();
            _startedAtUtc = DateTime.UtcNow;
            _cts = new CancellationTokenSource();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private async void OnTimerTick(object? sender, EventArgs e)
        {
            CancellationToken token = _cts?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested || _disposed)
                return;

            if (!await _tickLock.WaitAsync(0, token).ConfigureAwait(true))
                return;

            try
            {
                await TickAsync(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                FlashDebugConsole.Log("RuffleMonitor exception: " + ex.Message);
            }
            finally
            {
                _tickLock.Release();
            }
        }

        private async Task TickAsync(CancellationToken token)
        {
            if (_webView.CoreWebView2 == null)
                return;

            token.ThrowIfCancellationRequested();
            LastStatus = await RuffleInjector.GetStatusAsync(_webView).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            string? fatal = LastStatus.Errors.FirstOrDefault(IsFatalRuffleError);
            if (!string.IsNullOrWhiteSpace(fatal))
            {
                RaiseFailure(Tr("Erreur Ruffle : ") + fatal, LastStatus);
                return;
            }

            if (LastStatus.HasUsablePlayer)
            {
                _missingAfterReadyTicks = 0;

                if (!_readyRaised)
                {
                    _readyRaised = true;
                    _readyAtUtc = DateTime.UtcNow;
                    ReadyDetected?.Invoke(LastStatus);
                    _timer.Interval = TimeSpan.FromSeconds(3);
                }

                if (_readyAtUtc.HasValue && DateTime.UtcNow - _readyAtUtc.Value >= _stabilityWindow)
                    Stop();

                return;
            }

            if (_readyRaised)
            {
                _missingAfterReadyTicks++;
                if (_missingAfterReadyTicks >= 3)
                {
                    RaiseFailure(
                        Tr("Le lecteur Ruffle avait démarré, puis le contenu Flash a disparu ou s'est arrêté."),
                        LastStatus);
                }
                return;
            }

            if (DateTime.UtcNow - _startedAtUtc > _startTimeout)
            {
                string reason = LastStatus.ScriptLoaded
                    ? LastStatus.PlayerCount > 0
                        ? Tr("Ruffle a créé un lecteur, mais le fichier SWF n'a pas atteint l'état de lecture.")
                        : Tr("Ruffle est chargé, mais aucun lecteur Flash n'a été créé.")
                    : Tr("Le moteur Ruffle n'a pas pu être chargé dans le délai prévu.");

                RaiseFailure(reason, LastStatus);
            }
        }

        private static bool IsFatalRuffleError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return error.Contains("load-failed", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("load-timeout", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("player-load", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("panic", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("RuntimeError", StringComparison.OrdinalIgnoreCase);
        }

        private void RaiseFailure(string reason, RuffleStatus status)
        {
            if (_failureRaised)
                return;

            _failureRaised = true;
            FlashDebugConsole.Log("RuffleMonitor failure: " + reason);
            FailureDetected?.Invoke(new RuffleFailureInfo(reason, status));
            Stop();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Stop();
            _timer.Tick -= OnTimerTick;
            _tickLock.Dispose();
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
