using MyHomelabBrowser.classes.CloudTorrent.Models;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    public sealed class CloudTorrentApiClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly bool _ownsClient;

        public CloudTorrentApiClient(HttpClient? httpClient = null)
        {
            if (httpClient != null)
            {
                _httpClient = httpClient;
                _ownsClient = false;
            }
            else
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip |
                                             DecompressionMethods.Deflate |
                                             DecompressionMethods.Brotli
                };

                _httpClient = new HttpClient(handler, disposeHandler: true);
                _ownsClient = true;
            }

            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("MyHomelabBrowser-CloudTorrent", "2.0"));
        }

        public Task<CloudTorrentAccount> GetAccountAsync(
            string serverUrl,
            string apiKey,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<CloudTorrentAccount>(
                HttpMethod.Get,
                serverUrl,
                apiKey,
                "/me",
                body: null,
                timeout: TimeSpan.FromSeconds(20),
                cancellationToken);
        }

        public Task<CloudTorrentMediaAnalysisResponse> AnalyzeMediaAsync(
            string serverUrl,
            string apiKey,
            string url,
            string? referer,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<CloudTorrentMediaAnalysisResponse>(
                HttpMethod.Post,
                serverUrl,
                apiKey,
                "/media/analyze",
                new { url, referer },
                TimeSpan.FromSeconds(90),
                cancellationToken);
        }

        public Task<CloudTorrentActionResult> AddTorrentAsync(
            string serverUrl,
            string apiKey,
            string source,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<CloudTorrentActionResult>(
                HttpMethod.Post,
                serverUrl,
                apiKey,
                "/torrents",
                new { source },
                TimeSpan.FromSeconds(90),
                cancellationToken);
        }

        public Task<CloudTorrentActionResult> AddHosterJobAsync(
            string serverUrl,
            string apiKey,
            string url,
            string? referer,
            string? title,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<CloudTorrentActionResult>(
                HttpMethod.Post,
                serverUrl,
                apiKey,
                "/host-files/jobs",
                new { url, referer, title },
                TimeSpan.FromSeconds(120),
                cancellationToken);
        }

        public Task<CloudTorrentActionResult> AddMediaJobAsync(
            string serverUrl,
            string apiKey,
            object payload,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<CloudTorrentActionResult>(
                HttpMethod.Post,
                serverUrl,
                apiKey,
                "/media/jobs",
                payload,
                TimeSpan.FromSeconds(120),
                cancellationToken);
        }

        public Task<TResponse> SendAsync<TResponse>(
            HttpMethod method,
            string serverUrl,
            string apiKey,
            string apiPath,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<TResponse>(method, serverUrl, apiKey, apiPath, null, timeout, cancellationToken);
        }

        public async Task<TResponse> SendAsync<TResponse>(
            HttpMethod method,
            string serverUrl,
            string apiKey,
            string apiPath,
            object? body,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            string normalizedKey = (apiKey ?? string.Empty).Trim();
            if (normalizedKey.Length == 0)
                throw new ArgumentException("La clé API est vide.", nameof(apiKey));

            using var request = new HttpRequestMessage(method, CloudTorrentUrl.BuildApiUri(serverUrl, apiPath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedKey);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            if (body != null)
            {
                string json = JsonSerializer.Serialize(body, JsonOptions);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CloudTorrentApiException(
                    "Le site CloudTorrent ne répond pas dans le délai prévu.",
                    innerException: ex);
            }
            catch (HttpRequestException ex)
            {
                throw new CloudTorrentApiException(
                    "Connexion au site CloudTorrent impossible : " + ex.Message,
                    innerException: ex);
            }

            using (response)
            {
                string payload = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    throw CreateApiException(response.StatusCode, payload);

                try
                {
                    TResponse? result = JsonSerializer.Deserialize<TResponse>(payload, JsonOptions);
                    if (result == null)
                        throw new JsonException("Réponse vide.");

                    return result;
                }
                catch (JsonException ex)
                {
                    throw new CloudTorrentApiException(
                        "Le site a répondu, mais sa réponse API n’est pas compatible.",
                        response.StatusCode,
                        innerException: ex);
                }
            }
        }

        private static CloudTorrentApiException CreateApiException(HttpStatusCode statusCode, string payload)
        {
            string message = $"Erreur HTTP {(int)statusCode}.";
            string? code = null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                    message = error.GetString() ?? message;
                else if (root.TryGetProperty("message", out JsonElement apiMessage) && apiMessage.ValueKind == JsonValueKind.String)
                    message = apiMessage.GetString() ?? message;

                if (root.TryGetProperty("code", out JsonElement errorCode) && errorCode.ValueKind == JsonValueKind.String)
                    code = errorCode.GetString();
            }
            catch (JsonException)
            {
                if (!string.IsNullOrWhiteSpace(payload))
                    message = payload.Trim().Length > 300 ? payload.Trim()[..300] : payload.Trim();
            }

            return new CloudTorrentApiException(message, statusCode, code);
        }

        public void Dispose()
        {
            if (_ownsClient)
                _httpClient.Dispose();
        }
    }
}
