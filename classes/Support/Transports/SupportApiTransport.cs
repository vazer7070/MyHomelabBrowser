using MyHomelabBrowser.classes.Support.Models;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Support.Transports
{
    public sealed class SupportApiTransport : ISupportTransport, IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        public SupportApiTransport(HttpClient? httpClient = null)
        {
            if (httpClient != null)
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
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
                _ownsHttpClient = true;
            }

            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("PommeBrowser-Support", "1.0"));
        }

        public string Name => Tr("API de support");

        public bool CanAttempt => SupportApiConfiguration.ResolveReportUri() != null;

        public async Task<SupportSubmissionResult> SendAsync(
            SupportReportRequest report,
            SupportAttachment? attachment,
            CancellationToken cancellationToken = default)
        {
            Uri reportUri = SupportApiConfiguration.ResolveReportUri()
                ?? throw new SupportApiUnavailableException(
                    Tr("L’API de support n’est pas encore configurée dans PommeBrowser."));

            using var request = new HttpRequestMessage(HttpMethod.Post, reportUri);
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.TryAddWithoutValidation("X-PommeBrowser-Version", report.ClientVersion);
            request.Headers.TryAddWithoutValidation("X-PommeBrowser-Report-Id", report.ClientReportId);

            using var form = new MultipartFormDataContent();
            string reportJson = JsonSerializer.Serialize(report, JsonOptions);
            form.Add(
                new StringContent(reportJson, Encoding.UTF8, "application/json"),
                "report");

            if (attachment is { IsEmpty: false })
            {
                var attachmentContent = new ByteArrayContent(attachment.Content);
                attachmentContent.Headers.ContentType = MediaTypeHeaderValue.Parse(attachment.ContentType);
                form.Add(attachmentContent, "log", SanitizeFileName(attachment.FileName));
            }

            request.Content = form;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(SupportApiConfiguration.RequestTimeout);

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
                throw new SupportApiUnavailableException(
                    Tr("L’API de support ne répond pas dans le délai prévu."), ex);
            }
            catch (HttpRequestException ex)
            {
                throw new SupportApiUnavailableException(
                    Tr("Connexion à l’API de support impossible."), ex);
            }

            using (response)
            {
                string responseBody = await response.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                if (IsEndpointUnavailable(response.StatusCode))
                {
                    throw new SupportApiUnavailableException(
                        Tr("L’endpoint de support n’est pas disponible (HTTP {0}).", (int)response.StatusCode));
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SupportApiRejectedException(
                        response.StatusCode,
                        ReadSafeApiError(responseBody, response.StatusCode));
                }

                SupportApiResponse? apiResponse = TryReadResponse(responseBody);
                return new SupportSubmissionResult
                {
                    Success = true,
                    ReportId = string.IsNullOrWhiteSpace(apiResponse?.ReportId)
                        ? report.ClientReportId
                        : apiResponse.ReportId.Trim(),
                    Message = string.IsNullOrWhiteSpace(apiResponse?.Message)
                        ? Tr("Le rapport a été transmis au support.")
                        : apiResponse.Message.Trim(),
                    Channel = SupportDeliveryChannel.BackendApi,
                    UsedFallback = false
                };
            }
        }

        private static bool IsEndpointUnavailable(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.NotFound
                || statusCode == HttpStatusCode.MethodNotAllowed
                || statusCode == HttpStatusCode.NotImplemented
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout;
        }

        private static string ReadSafeApiError(string responseBody, HttpStatusCode statusCode)
        {
            string fallback = Tr("L’API de support a refusé le rapport (HTTP {0}).", (int)statusCode);
            if (string.IsNullOrWhiteSpace(responseBody))
                return fallback;

            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBody);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return Limit(message.GetString(), 350, fallback);
                }

                if (root.TryGetProperty("error", out JsonElement error) &&
                    error.ValueKind == JsonValueKind.String)
                {
                    return Limit(error.GetString(), 350, fallback);
                }
            }
            catch (JsonException)
            {
            }

            return Limit(responseBody, 350, fallback);
        }

        private static SupportApiResponse? TryReadResponse(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return null;

            try
            {
                return JsonSerializer.Deserialize<SupportApiResponse>(responseBody, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            string value = Path.GetFileName(fileName);
            return string.IsNullOrWhiteSpace(value) ? "pommebrowser-log.txt" : value;
        }

        private static string Limit(string? value, int maxLength, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            string normalized = value.Trim();
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength] + "…";
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }

        private sealed class SupportApiResponse
        {
            public bool Accepted { get; set; }
            public string ReportId { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
        }
    }
}
