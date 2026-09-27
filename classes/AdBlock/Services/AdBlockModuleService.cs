using MyHomelabBrowser.classes.AdBlock.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.AdBlock.Services
{
    public sealed class AdBlockModuleService : IDisposable
    {
        private readonly SemaphoreSlim _reloadLock = new(1, 1);
        private long _sessionBlockedCount;
        private long _lastStatisticsNotificationTick;
        private bool _initialized;
        private string _statusMessage = Tr("Initialisation…");

        public AdBlockSettingsService SettingsService { get; }
        public AdBlockFilterListService FilterLists { get; }
        public AdBlockEngine Engine { get; } = new();

        public event Action? StateChanged;
        public event Action? RulesChanged;
        public event Action? StatisticsChanged;
        public event Action<string>? StatusChanged;

        public AdBlockModuleService()
        {
            SettingsService = new AdBlockSettingsService();
            FilterLists = new AdBlockFilterListService(SettingsService);

            SettingsService.SettingsChanged += _ => StateChanged?.Invoke();
            FilterLists.StatusChanged += message =>
            {
                _statusMessage = message;
                StatusChanged?.Invoke(message);
            };
        }

        /// <summary>
        /// Paramètres courants, en lecture seule : passer par les méthodes Set… pour les modifier.
        /// </summary>
        public AdBlockSettings Settings => SettingsService.Snapshot;

        public async Task InitializeAsync()
        {
            if (_initialized)
                return;

            _initialized = true;
            await ReloadRulesFromDiskAsync().ConfigureAwait(false);

            if (FilterLists.NeedsUpdate(Settings))
                _ = UpdateListsAsync(force: false);
        }

        public async Task ReloadForCurrentProfileAsync()
        {
            SettingsService.ReloadForCurrentProfile();
            await ReloadRulesFromDiskAsync().ConfigureAwait(false);

            if (FilterLists.NeedsUpdate(Settings))
                _ = UpdateListsAsync(force: false);
        }

        public async Task<AdBlockUpdateResult> UpdateListsAsync(bool force)
        {
            AdBlockUpdateResult result = await FilterLists.UpdateAsync(force).ConfigureAwait(false);
            if (result.UpdatedListCount > 0)
                await ReloadRulesFromDiskAsync().ConfigureAwait(false);
            return result;
        }

        public bool IsFilteringEnabledForHost(string? host)
        {
            AdBlockSettings settings = Settings;
            if (!settings.Enabled)
                return false;

            string normalized = AdBlockDomain.NormalizeHost(host);
            if (normalized.Length == 0)
                return false;

            if (settings.BypassPrivateNetworks && AdBlockDomain.IsPrivateOrLocalHost(normalized))
                return false;

            return !IsAllowlisted(settings, normalized);
        }

        // Les domaines autorisés sont normalisés à l'enregistrement.
        private static bool IsAllowlisted(AdBlockSettings settings, string normalizedHost)
        {
            foreach (string domain in settings.AllowlistedDomains)
            {
                if (AdBlockDomain.IsSameOrSubdomain(normalizedHost, domain))
                    return true;
            }
            return false;
        }

        public bool ShouldBlock(Uri requestUri, string documentHost, AdBlockResourceType resourceType)
        {
            if (!IsFilteringEnabledForHost(documentHost))
                return false;

            if (requestUri.Scheme is not ("http" or "https" or "ws" or "wss"))
                return false;

            // Le document principal n'est jamais annulé par ce moteur afin d'éviter les pages blanches.
            if (resourceType == AdBlockResourceType.Document)
                return false;

            string normalizedDocumentHost = AdBlockDomain.NormalizeHost(documentHost);
            var context = new AdBlockRequestContext
            {
                RequestUri = requestUri,
                DocumentHost = normalizedDocumentHost,
                ResourceType = resourceType,
                IsThirdParty = !AdBlockDomain.IsSameSite(normalizedDocumentHost, requestUri.Host)
            };

            return Engine.ShouldBlock(context);
        }

        public IReadOnlyList<string> GetCosmeticSelectors(string host)
        {
            AdBlockSettings settings = Settings;
            if (!settings.CosmeticFiltering || !IsFilteringEnabledForHost(host))
                return Array.Empty<string>();

            return Engine.GetCosmeticSelectors(host);
        }

        public void SetGlobalEnabled(bool enabled)
            => SettingsService.Update(settings => settings.Enabled = enabled);

        public void SetCosmeticFiltering(bool enabled)
            => SettingsService.Update(settings => settings.CosmeticFiltering = enabled);

        public void SetAutoUpdate(bool enabled)
            => SettingsService.Update(settings => settings.AutoUpdate = enabled);

        public void SetBypassPrivateNetworks(bool enabled)
            => SettingsService.Update(settings => settings.BypassPrivateNetworks = enabled);

        public void SetSubscriptionEnabled(string id, bool enabled)
        {
            SettingsService.Update(settings =>
            {
                AdBlockSubscription? subscription = settings.Subscriptions
                    .FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (subscription != null)
                    subscription.Enabled = enabled;
            });
            _ = ReloadRulesFromDiskAsync();
        }

        public void SetDefaultSubscriptions(bool easyListEnabled, bool easyPrivacyEnabled)
        {
            SettingsService.Update(settings =>
            {
                AdBlockSubscription? easyList = settings.Subscriptions
                    .FirstOrDefault(item => item.Id.Equals("easylist", StringComparison.OrdinalIgnoreCase));
                AdBlockSubscription? easyPrivacy = settings.Subscriptions
                    .FirstOrDefault(item => item.Id.Equals("easyprivacy", StringComparison.OrdinalIgnoreCase));

                if (easyList != null)
                    easyList.Enabled = easyListEnabled;
                if (easyPrivacy != null)
                    easyPrivacy.Enabled = easyPrivacyEnabled;
            });
            _ = ReloadRulesFromDiskAsync();
        }

        public void SetSiteAllowed(string host, bool allowed)
        {
            string normalized = AdBlockDomain.NormalizeHost(host);
            if (normalized.Length == 0)
                return;

            SettingsService.Update(settings =>
            {
                settings.AllowlistedDomains.RemoveAll(domain => domain.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (allowed)
                    settings.AllowlistedDomains.Add(normalized);
            });
        }

        public bool IsSiteAllowed(string host)
        {
            string normalized = AdBlockDomain.NormalizeHost(host);
            return normalized.Length > 0 && IsAllowlisted(Settings, normalized);
        }

        public void RecordBlockedRequest()
        {
            long count = Interlocked.Increment(ref _sessionBlockedCount);
            long now = Environment.TickCount64;
            long previous = Interlocked.Read(ref _lastStatisticsNotificationTick);

            if (count == 1 || count % 10 == 0 || now - previous >= 200)
            {
                Interlocked.Exchange(ref _lastStatisticsNotificationTick, now);
                StatisticsChanged?.Invoke();
            }
        }

        public AdBlockModuleSnapshot GetSnapshot() => new()
        {
            Enabled = Settings.Enabled,
            IsReady = Engine.NetworkRuleCount > 0,
            NetworkRuleCount = Engine.NetworkRuleCount,
            CosmeticRuleCount = Engine.CosmeticRuleCount,
            SessionBlockedCount = Interlocked.Read(ref _sessionBlockedCount),
            LastSuccessfulUpdateUtc = Settings.LastSuccessfulUpdateUtc,
            StatusMessage = _statusMessage
        };

        private async Task ReloadRulesFromDiskAsync()
        {
            await _reloadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _statusMessage = Tr("Chargement des règles…");
                StatusChanged?.Invoke(_statusMessage);

                var sources = FilterLists.LoadAvailableLists();
                await Task.Run(() => Engine.ReplaceRules(sources)).ConfigureAwait(false);

                _statusMessage = Tr("{0:N0} règles réseau et {1:N0} règles visuelles chargées.", Engine.NetworkRuleCount, Engine.CosmeticRuleCount);
                RulesChanged?.Invoke();
                StatusChanged?.Invoke(_statusMessage);
                StateChanged?.Invoke();
            }
            finally
            {
                _reloadLock.Release();
            }
        }

        public void Dispose()
        {
            FilterLists.Dispose();
            _reloadLock.Dispose();
        }
    }
}
