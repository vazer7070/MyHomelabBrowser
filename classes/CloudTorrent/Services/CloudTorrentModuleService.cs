using MyHomelabBrowser.classes.CloudTorrent.Models;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    public sealed class CloudTorrentModuleService : IDisposable
    {
        private readonly CloudTorrentConfigurationService _configurationService;
        private readonly CloudTorrentApiClient _apiClient;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public CloudTorrentModuleService(
            CloudTorrentConfigurationService? configurationService = null,
            CloudTorrentApiClient? apiClient = null)
        {
            _configurationService = configurationService ?? new CloudTorrentConfigurationService();
            _apiClient = apiClient ?? new CloudTorrentApiClient();
            Snapshot = CreateNotConfiguredSnapshot();
        }

        public event Action<CloudTorrentModuleSnapshot>? StateChanged;

        public CloudTorrentModuleSnapshot Snapshot { get; private set; }

        public CloudTorrentConfiguration Configuration => _configurationService.Current.Clone();

        public bool IsActive => Snapshot.IsActive;

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                CloudTorrentConfiguration current = _configurationService.Current;
                if (Snapshot.IsActive &&
                    string.Equals(Snapshot.ServerUrl, current.ServerUrl, StringComparison.OrdinalIgnoreCase) &&
                    Snapshot.AutoAnalyzePages == current.AutoAnalyzePages)
                {
                    return;
                }

                await ValidateStoredConfigurationCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ReloadForCurrentProfileAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                _configurationService.ReloadForCurrentProfile();
                SetSnapshot(CreateNotConfiguredSnapshot("Chargement du profil CloudTorrent…"));
                await ValidateStoredConfigurationCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<CloudTorrentModuleSnapshot> ConfigureAsync(
            string serverUrl,
            string? apiKey,
            bool autoAnalyzePages,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            CloudTorrentModuleSnapshot previousSnapshot = Snapshot;

            try
            {
                ThrowIfDisposed();

                string normalizedUrl = CloudTorrentUrl.NormalizeServerUrl(serverUrl);
                string normalizedKey = (apiKey ?? string.Empty).Trim();

                if (normalizedKey.Length == 0)
                    normalizedKey = _configurationService.ReadApiKey() ?? string.Empty;

                if (!CloudTorrentUrl.LooksLikeApiKey(normalizedKey))
                    throw new ArgumentException("La clé API ne semble pas valide.", nameof(apiKey));

                SetSnapshot(new CloudTorrentModuleSnapshot
                {
                    Status = CloudTorrentConnectionStatus.Validating,
                    ServerUrl = normalizedUrl,
                    Message = "Vérification de la clé API…",
                    AutoAnalyzePages = autoAnalyzePages,
                    HasStoredApiKey = normalizedKey.Length > 0
                });

                CloudTorrentAccount account = await _apiClient.GetAccountAsync(
                    normalizedUrl,
                    normalizedKey,
                    cancellationToken).ConfigureAwait(false);

                var configuration = new CloudTorrentConfiguration
                {
                    ServerUrl = normalizedUrl,
                    AutoAnalyzePages = autoAnalyzePages
                };

                _configurationService.Save(configuration, normalizedKey);
                SetSnapshot(CreateActiveSnapshot(configuration, account));
                return Snapshot;
            }
            catch (Exception ex)
            {
                if (previousSnapshot.IsActive)
                    SetSnapshot(previousSnapshot);
                else
                    SetSnapshot(CreateFailureSnapshot(
                        serverUrl,
                        autoAnalyzePages,
                        _configurationService.HasStoredApiKey,
                        ex));

                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                _configurationService.Clear();
                SetSnapshot(CreateNotConfiguredSnapshot("CloudTorrent est déconnecté."));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<CloudTorrentModuleSnapshot> RefreshAccountAsync(
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                (string serverUrl, string apiKey) = GetActiveCredentials();
                CloudTorrentAccount account = await _apiClient.GetAccountAsync(
                    serverUrl, apiKey, cancellationToken).ConfigureAwait(false);

                SetSnapshot(CreateActiveSnapshot(_configurationService.Current, account));
                return Snapshot;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<CloudTorrentMediaAnalysis> AnalyzeMediaAsync(
            string url,
            string? referer = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            EnsurePermission(CloudTorrentPermission.MediaAnalyze,
                "Cette clé API ne permet pas d’analyser les pages et les médias.");

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("L’adresse à analyser doit être une URL HTTP ou HTTPS.", nameof(url));
            }

            (string serverUrl, string apiKey) = GetActiveCredentials();
            CloudTorrentMediaAnalysisResponse result = await _apiClient.AnalyzeMediaAsync(
                serverUrl, apiKey, url.Trim(), NormalizeReferer(referer, url), cancellationToken)
                .ConfigureAwait(false);

            return result.Analysis;
        }

        public async Task<CloudTorrentQueueResult> QueueItemAsync(
            CloudTorrentDetectedItem item,
            string? referer = null,
            CloudTorrentQualityChoice? qualityChoice = null,
            CloudTorrentMediaAnalysis? analysis = null,
            CancellationToken cancellationToken = default)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            ThrowIfDisposed();
            (string serverUrl, string apiKey) = GetActiveCredentials();
            CloudTorrentActionResult result;

            switch (item.Type)
            {
                case CloudTorrentDetectedItemType.Torrent:
                    EnsurePermission(CloudTorrentPermission.TorrentsWrite,
                        "Cette clé API ne permet pas d’ajouter des torrents.");
                    result = await _apiClient.AddTorrentAsync(
                        serverUrl, apiKey, item.Url, cancellationToken).ConfigureAwait(false);
                    break;

                case CloudTorrentDetectedItemType.Hoster:
                    EnsurePermission(CloudTorrentPermission.FilesWrite,
                        "Cette clé API ne permet pas d’ajouter des fichiers d’hébergeur.");
                    result = await _apiClient.AddHosterJobAsync(
                        serverUrl, apiKey, item.Url, NormalizeReferer(referer, item.Url),
                        item.Label, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    EnsurePermission(CloudTorrentPermission.MediaWrite,
                        "Cette clé API ne permet pas d’ajouter des vidéos.");

                    CloudTorrentMediaProfile profile = Snapshot.Account?.MediaProfile ?? new CloudTorrentMediaProfile();
                    string choiceId = qualityChoice?.Id ?? (profile.Mode == "audio" ? "audio" : profile.Quality);
                    bool exactFormat = string.Equals(qualityChoice?.Kind, "format", StringComparison.OrdinalIgnoreCase);
                    string mode = qualityChoice?.IsAudio == true || string.Equals(choiceId, "audio", StringComparison.OrdinalIgnoreCase)
                        ? "audio"
                        : "video";

                    var payload = new
                    {
                        url = item.Url,
                        referer = NormalizeReferer(referer, item.Url),
                        title = string.IsNullOrWhiteSpace(analysis?.Title) ? item.Label : analysis.Title,
                        quality = mode == "audio" ? "audio" : (exactFormat ? "best" : choiceId),
                        formatId = exactFormat ? choiceId : null,
                        mode,
                        includeSubtitles = profile.IncludeSubtitles,
                        maxRetries = Math.Clamp(profile.MaxRetries, 0, 5)
                    };

                    result = await _apiClient.AddMediaJobAsync(
                        serverUrl, apiKey, payload, cancellationToken).ConfigureAwait(false);
                    break;
            }

            string fallback = item.Type switch
            {
                CloudTorrentDetectedItemType.Torrent => "Torrent",
                CloudTorrentDetectedItemType.Hoster => "Fichier",
                _ => "Vidéo"
            };

            string title = result.GetTitle(fallback);
            return new CloudTorrentQueueResult
            {
                Success = true,
                Duplicate = result.Duplicate,
                Item = item,
                Message = result.Duplicate
                    ? $"Déjà présent : {title}"
                    : item.Type == CloudTorrentDetectedItemType.Torrent
                        ? "Torrent ajouté au débrideur."
                        : $"Ajouté à CloudTorrent : {title}"
            };
        }

        public bool HasPermission(string permission)
        {
            return Snapshot.HasPermission(permission);
        }

        private (string ServerUrl, string ApiKey) GetActiveCredentials()
        {
            CloudTorrentModuleSnapshot snapshot = Snapshot;
            string? apiKey = _configurationService.ReadApiKey();

            if (!snapshot.IsActive || string.IsNullOrWhiteSpace(snapshot.ServerUrl) || string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Le module CloudTorrent n’est pas connecté.");

            return (snapshot.ServerUrl, apiKey);
        }

        private void EnsurePermission(string permission, string message)
        {
            if (!Snapshot.HasPermission(permission))
                throw new InvalidOperationException(message);
        }

        private static string? NormalizeReferer(string? referer, string target)
        {
            string value = (referer ?? string.Empty).Trim();
            if (value.Length == 0 || string.Equals(value, target, StringComparison.OrdinalIgnoreCase))
                return null;

            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? value
                : null;
        }

        private async Task ValidateStoredConfigurationCoreAsync(CancellationToken cancellationToken)
        {
            CloudTorrentConfiguration configuration = _configurationService.Current;
            string? apiKey;

            try
            {
                apiKey = _configurationService.ReadApiKey();
            }
            catch (Exception ex)
            {
                SetSnapshot(CreateFailureSnapshot(
                    configuration.ServerUrl,
                    configuration.AutoAnalyzePages,
                    _configurationService.HasStoredApiKey,
                    ex));
                return;
            }

            if (!configuration.HasServerUrl || string.IsNullOrWhiteSpace(apiKey))
            {
                SetSnapshot(CreateNotConfiguredSnapshot());
                return;
            }

            SetSnapshot(new CloudTorrentModuleSnapshot
            {
                Status = CloudTorrentConnectionStatus.Validating,
                ServerUrl = configuration.ServerUrl,
                Message = "Connexion à CloudTorrent…",
                AutoAnalyzePages = configuration.AutoAnalyzePages,
                HasStoredApiKey = true
            });

            try
            {
                CloudTorrentAccount account = await _apiClient.GetAccountAsync(
                    configuration.ServerUrl,
                    apiKey,
                    cancellationToken).ConfigureAwait(false);

                SetSnapshot(CreateActiveSnapshot(configuration, account));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetSnapshot(CreateFailureSnapshot(
                    configuration.ServerUrl,
                    configuration.AutoAnalyzePages,
                    true,
                    ex));
            }
        }

        private CloudTorrentModuleSnapshot CreateNotConfiguredSnapshot(string? message = null)
        {
            CloudTorrentConfiguration configuration = _configurationService.Current;
            return new CloudTorrentModuleSnapshot
            {
                Status = CloudTorrentConnectionStatus.NotConfigured,
                ServerUrl = configuration.ServerUrl,
                Message = message ?? "Renseignez l’adresse du site et une clé API valide.",
                AutoAnalyzePages = configuration.AutoAnalyzePages,
                HasStoredApiKey = _configurationService.HasStoredApiKey
            };
        }

        private static CloudTorrentModuleSnapshot CreateActiveSnapshot(
            CloudTorrentConfiguration configuration,
            CloudTorrentAccount account)
        {
            return new CloudTorrentModuleSnapshot
            {
                Status = CloudTorrentConnectionStatus.Active,
                ServerUrl = configuration.ServerUrl,
                Message = $"Connecté en tant que {account.User.Username}.",
                Username = account.User.Username,
                ApiVersion = account.Version,
                AutoAnalyzePages = configuration.AutoAnalyzePages,
                HasStoredApiKey = true,
                Permissions = account.Permissions.ToArray(),
                Account = account,
                LastValidatedAt = DateTimeOffset.Now
            };
        }

        private static CloudTorrentModuleSnapshot CreateFailureSnapshot(
            string? serverUrl,
            bool autoAnalyzePages,
            bool hasStoredApiKey,
            Exception exception)
        {
            CloudTorrentConnectionStatus status;
            string message = exception.Message;

            if (exception is ArgumentException || exception is UriFormatException)
            {
                status = CloudTorrentConnectionStatus.InvalidConfiguration;
            }
            else if (exception is CloudTorrentApiException apiException)
            {
                status = apiException.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => CloudTorrentConnectionStatus.InvalidApiKey,
                    HttpStatusCode.Forbidden => CloudTorrentConnectionStatus.Forbidden,
                    null => CloudTorrentConnectionStatus.ServerUnavailable,
                    _ => CloudTorrentConnectionStatus.Error
                };
            }
            else
            {
                status = CloudTorrentConnectionStatus.Error;
            }

            return new CloudTorrentModuleSnapshot
            {
                Status = status,
                ServerUrl = serverUrl?.Trim() ?? string.Empty,
                Message = message,
                AutoAnalyzePages = autoAnalyzePages,
                HasStoredApiKey = hasStoredApiKey
            };
        }

        private void SetSnapshot(CloudTorrentModuleSnapshot snapshot)
        {
            Snapshot = snapshot;
            StateChanged?.Invoke(snapshot);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CloudTorrentModuleService));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _apiClient.Dispose();
            _gate.Dispose();
        }
    }
}
