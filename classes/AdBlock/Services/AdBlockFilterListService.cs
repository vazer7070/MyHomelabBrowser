using MyHomelabBrowser.classes.AdBlock.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.AdBlock.Services
{
    public sealed class AdBlockFilterListService : IDisposable
    {
        private readonly AdBlockSettingsService _settingsService;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _updateLock = new(1, 1);

        public event Action<string>? StatusChanged;

        public AdBlockFilterListService(AdBlockSettingsService settingsService)
        {
            _settingsService = settingsService;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(35)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PommeBrowser-AdBlock/1.0");
        }

        public IReadOnlyList<(string SourceName, string Content)> LoadAvailableLists()
        {
            var result = new List<(string SourceName, string Content)>
            {
                ("PommeBrowser intégré", BuiltInFallbackRules)
            };

            AdBlockSettings settings = _settingsService.Current;
            string listDirectory = GetListDirectory();

            foreach (AdBlockSubscription subscription in settings.Subscriptions.Where(item => item.Enabled))
            {
                string path = GetSubscriptionPath(listDirectory, subscription);
                if (!File.Exists(path))
                    continue;

                try
                {
                    result.Add((subscription.Name, File.ReadAllText(path)));
                }
                catch { }
            }

            return result;
        }

        public bool NeedsUpdate(AdBlockSettings settings)
        {
            if (!settings.AutoUpdate)
                return false;

            DateTimeOffset threshold = DateTimeOffset.UtcNow.AddHours(-settings.UpdateIntervalHours);
            string listDirectory = GetListDirectory();

            foreach (AdBlockSubscription subscription in settings.Subscriptions.Where(item => item.Enabled))
            {
                string path = GetSubscriptionPath(listDirectory, subscription);
                if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < threshold.UtcDateTime)
                    return true;
            }

            return false;
        }

        public async Task<AdBlockUpdateResult> UpdateAsync(bool force, CancellationToken cancellationToken = default)
        {
            await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AdBlockSettings settings = _settingsService.Current;
                if (!force && !NeedsUpdate(settings))
                    return new AdBlockUpdateResult(true, 0, "Les listes sont déjà à jour.");

                string listDirectory = GetListDirectory();
                Directory.CreateDirectory(listDirectory);
                int updated = 0;
                var errors = new List<string>();

                foreach (AdBlockSubscription subscription in settings.Subscriptions.Where(item => item.Enabled))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StatusChanged?.Invoke($"Téléchargement de {subscription.Name}…");

                    try
                    {
                        using HttpResponseMessage response = await _httpClient.GetAsync(subscription.Url, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                        if (!LooksLikeFilterList(content))
                            throw new InvalidDataException("Le contenu reçu ne ressemble pas à une liste de filtres.");

                        string path = GetSubscriptionPath(listDirectory, subscription);
                        string temporaryPath = path + ".tmp";
                        await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
                        File.Move(temporaryPath, path, overwrite: true);
                        updated++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.Add($"{subscription.Name} : {ex.Message}");
                    }
                }

                if (updated > 0)
                {
                    settings.LastSuccessfulUpdateUtc = DateTimeOffset.UtcNow;
                    _settingsService.Save(settings);
                }

                string message = errors.Count == 0
                    ? $"{updated} liste{(updated > 1 ? "s" : string.Empty)} mise{(updated > 1 ? "s" : string.Empty)} à jour."
                    : updated > 0
                        ? $"{updated} liste(s) mise(s) à jour. {errors.Count} échec(s)."
                        : "Impossible de mettre à jour les listes : " + string.Join(" | ", errors);

                StatusChanged?.Invoke(message);
                return new AdBlockUpdateResult(updated > 0 || errors.Count == 0, updated, message);
            }
            finally
            {
                _updateLock.Release();
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _updateLock.Dispose();
        }

        private string GetListDirectory()
            => Path.Combine(_settingsService.GetProfileDirectory(), "Lists");

        private static string GetSubscriptionPath(string directory, AdBlockSubscription subscription)
        {
            string safeId = string.Concat(subscription.Id.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
            return Path.Combine(directory, safeId + ".txt");
        }

        private static bool LooksLikeFilterList(string content)
        {
            if (string.IsNullOrWhiteSpace(content) || content.Length < 200)
                return false;

            return content.Contains("[Adblock", StringComparison.OrdinalIgnoreCase)
                || content.Contains("||", StringComparison.Ordinal)
                || content.Contains("##", StringComparison.Ordinal);
        }

        private const string BuiltInFallbackRules = """
! PommeBrowser — règles de secours originales
||doubleclick.net^
||googlesyndication.com^
||googleadservices.com^
||adservice.google.com^
||amazon-adsystem.com^
||adsystem.com^
||adnxs.com^
||criteo.com^
||criteo.net^
||taboola.com^
||outbrain.com^
||scorecardresearch.com^
||quantserve.com^
||zedo.com^
||pubmatic.com^
||rubiconproject.com^
||openx.net^
||casalemedia.com^
||moatads.com^
||adsrvr.org^
||google-analytics.com^$third-party
||hotjar.com^$third-party
||clarity.ms^$third-party
##.advertisement
##.advertising
##.ad-container
##.ad_container
##.ad-wrapper
##.ad_wrapper
##.adsbox
##.sponsored-content
##.sponsored-container
##ins.adsbygoogle
##[id^="google_ads_"]
##[id*="div-gpt-ad"]
##[id^="ad-"]
##[id^="ad_"]
##[class*=" sponsored-"]
##[class*="ad-container"]
##[class*="ad_wrapper"]
##[data-ad]
##[data-ad-slot]
##[data-ad-unit]
##[data-ad-container]
##[aria-label*="Advertisement"]
##[aria-label*="Publicité"]
""";
    }

    public sealed record AdBlockUpdateResult(bool Success, int UpdatedListCount, string Message);
}
