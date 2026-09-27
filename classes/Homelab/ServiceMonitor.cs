using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Homelab
{
    /// <summary>
    /// Vérifie périodiquement les services surveillés et signale les changements d'état.
    /// Le premier résultat d'un service ne déclenche pas d'alerte (pas de rafale au démarrage).
    /// </summary>
    public sealed class ServiceMonitor : IDisposable
    {
        private const int MaxParallelChecks = 6;

        private readonly ServiceHealthChecker _checker;
        private readonly Func<IReadOnlyList<HomelabService>> _servicesProvider;
        private readonly ConcurrentDictionary<Guid, ServiceCheckResult> _results = new();
        private readonly SemaphoreSlim _runLock = new(1, 1);
        private CancellationTokenSource? _loopCts;
        private TimeSpan _interval = TimeSpan.FromSeconds(60);

        public ServiceMonitor(Func<IReadOnlyList<HomelabService>> servicesProvider, ServiceHealthChecker? checker = null)
        {
            _servicesProvider = servicesProvider;
            _checker = checker ?? new ServiceHealthChecker();
        }

        /// <summary>
        /// Résultat d'une vérification (toujours levé, hors thread UI).
        /// </summary>
        public event Action<HomelabService, ServiceCheckResult>? Checked;

        /// <summary>
        /// Passage en ligne ↔ hors ligne après un premier état connu (hors thread UI).
        /// </summary>
        public event Action<HomelabService, ServiceState, ServiceState>? StateChanged;

        public event Action<HomelabService>? CheckStarted;

        public ServiceCheckResult GetResult(Guid id)
            => _results.TryGetValue(id, out ServiceCheckResult? result) ? result : ServiceCheckResult.Unknown;

        public bool IsRunning => _loopCts != null;

        public void Start(TimeSpan interval)
        {
            _interval = interval < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : interval;
            if (_loopCts != null)
                return;

            _loopCts = new CancellationTokenSource();
            _ = LoopAsync(_loopCts.Token);
        }

        public void Stop()
        {
            _loopCts?.Cancel();
            _loopCts = null;
        }

        /// <summary>
        /// Oublie les états connus (changement de profil).
        /// </summary>
        public void Reset() => _results.Clear();

        private async Task LoopAsync(CancellationToken token)
        {
            try
            {
                // Laisse le navigateur démarrer avant la première vague de vérifications.
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    await CheckAllAsync(token).ConfigureAwait(false);
                    await Task.Delay(_interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task CheckAllAsync(CancellationToken token = default)
        {
            if (!await _runLock.WaitAsync(0, token).ConfigureAwait(false))
                return;

            try
            {
                List<HomelabService> services = _servicesProvider().Where(s => s.Monitor).ToList();
                using var throttle = new SemaphoreSlim(MaxParallelChecks);

                await Task.WhenAll(services.Select(async service =>
                {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await CheckOneAsync(service, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                })).ConfigureAwait(false);
            }
            finally
            {
                _runLock.Release();
            }
        }

        public async Task<ServiceCheckResult> CheckOneAsync(HomelabService service, CancellationToken token = default)
        {
            CheckStarted?.Invoke(service);
            ServiceCheckResult result = await _checker.CheckAsync(service.Url, token).ConfigureAwait(false);

            ServiceCheckResult? previous = _results.TryGetValue(service.Id, out ServiceCheckResult? known) ? known : null;
            _results[service.Id] = result;

            Checked?.Invoke(service, result);

            if (previous != null &&
                previous.State != ServiceState.Unknown &&
                IsUp(previous.State) != IsUp(result.State))
            {
                StateChanged?.Invoke(service, previous.State, result.State);
            }

            return result;
        }

        private static bool IsUp(ServiceState state) => state is ServiceState.Online or ServiceState.Degraded;

        public void Dispose()
        {
            Stop();
            _checker.Dispose();
            _runLock.Dispose();
        }
    }
}
