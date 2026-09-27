using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Homelab
{
    /// <summary>
    /// Vérifie qu'un service répond. Toute réponse HTTP compte : une page de connexion
    /// (401/403) signifie que le service tourne. Les certificats auto-signés sont acceptés
    /// pour les adresses locales, uniquement pour ce test : la navigation garde ses règles.
    /// </summary>
    public sealed class ServiceHealthChecker : IDisposable
    {
        private readonly HttpClient _client;

        public ServiceHealthChecker(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
        {
            _client = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(6)
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("PommeBrowser-ServiceCheck/1.0");
        }

        private static SocketsHttpHandler CreateHandler() => new()
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                        return true;

                    string host = sender is SslStream stream ? stream.TargetHostName : string.Empty;
                    return UrlResolver.IsLocalHost(host);
                }
            }
        };

        public async Task<ServiceCheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return new ServiceCheckResult(ServiceState.Offline, null, null, "adresse invalide", DateTime.Now);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using HttpResponseMessage response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();
                int code = (int)response.StatusCode;
                ServiceState state = code >= 500 ? ServiceState.Degraded : ServiceState.Online;
                return new ServiceCheckResult(state, code, stopwatch.Elapsed, null, DateTime.Now);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ServiceCheckResult(ServiceState.Offline, null, null, "pas de réponse", DateTime.Now);
            }
            catch (HttpRequestException ex)
            {
                return new ServiceCheckResult(ServiceState.Offline, null, null, Describe(ex), DateTime.Now);
            }
        }

        private static string Describe(HttpRequestException ex)
        {
            for (Exception? inner = ex; inner != null; inner = inner.InnerException)
            {
                switch (inner)
                {
                    case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                        return "connexion refusée";
                    case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                        return "nom introuvable";
                    case SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable }:
                        return "injoignable";
                    case AuthenticationException:
                        return "certificat refusé";
                }
            }

            return ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "nom introuvable",
                HttpRequestError.ConnectionError => "injoignable",
                HttpRequestError.SecureConnectionError => "erreur TLS",
                _ => "erreur réseau"
            };
        }

        public void Dispose() => _client.Dispose();
    }
}
