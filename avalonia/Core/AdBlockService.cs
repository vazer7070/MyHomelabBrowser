using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;
using MyHomelabBrowser.classes.Localization;
using PommeBrowser.Engine;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Bloqueur de publicités et de pisteurs. Mêmes listes et mêmes réglages que les autres
    /// éditions (dossier AdBlock du profil). Avec WebKit (Linux, macOS), les listes sont converties
    /// en règles de blocage de contenu (ContentRuleConverter), compilées une fois puis gardées en
    /// cache. Avec WebView2 (Windows), chaque requête passe par le moteur de règles de l'édition
    /// WPF (AdBlockModuleService, AdBlockTabSession). Un site autorisé, ou du réseau local, est
    /// simplement affiché sans le filtre.
    /// </summary>
    public sealed class AdBlockService : IDisposable
    {
        const string IdentifierPrefix = "pommebrowser-";

        /// <summary>Réglages lus par le moteur à chaque chargement (fil du moteur) : jamais modifiés, remplacés.</summary>
        sealed record Policy(bool Enabled, bool BypassPrivateNetworks, string[] Allowlist);

        readonly AdBlockModuleService _module;
        readonly AdBlockSettingsService _settings;
        readonly AdBlockFilterListService _lists;
        readonly string _storeDirectory;
        readonly string _statePath;

        volatile Policy _policy = new(false, true, Array.Empty<string>());
        string? _filterId;
        bool _building;
        bool _buildAgain;

        public AdBlockService()
        {
            // Réglages et listes du module commun : un seul fichier de réglages par profil.
            _module = AdBlockModuleHost.Current;
            _settings = _module.SettingsService;
            _lists = _module.FilterLists;
            _storeDirectory = AppPaths.Cache("content-filters");
            _statePath = Path.Combine(_storeDirectory, "state.json");
            UpdatePolicy();
            EngineHost.ContentFilterPolicy = ShouldFilter;
            if (EngineHost.Kind == EngineKind.WebView2)
            {
                EngineHost.RequestFilter = _module;
                _module.RulesChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatus);
            }
        }

        /// <summary>Filtre remplacé, réglages ou état modifiés (fil de l'interface).</summary>
        public event Action? Changed;

        public AdBlockSettings Settings => _settings.Snapshot;

        public string Status { get; private set; } = Tr("Préparation du bloqueur de publicités…");

        public int RuleCount { get; private set; }

        public bool IsReady { get; private set; }

        /// <summary>Le moteur de ce système sait-il appliquer les règles ?</summary>
        public static bool IsSupported => EngineHost.Kind is EngineKind.WebKitGtk or EngineKind.WebView2 or EngineKind.WebKitApple;

        /// <summary>Requêtes filtrées une à une par PommeBrowser (WebView2) plutôt que par le moteur.</summary>
        static bool FiltersRequests => EngineHost.Kind == EngineKind.WebView2;

        public async Task StartAsync()
        {
            if (!IsSupported)
            {
                Status = Tr("Bloqueur indisponible avec ce moteur");
                Changed?.Invoke();
                return;
            }

            if (FiltersRequests)
            {
                await _module.InitializeAsync();
                UpdateStatus();
                return;
            }

            await RebuildAsync();
            if (_lists.NeedsUpdate(_settings.Current))
                await UpdateListsAsync(force: false);
        }

        public void UpdateSettings(Action<AdBlockSettings> update, bool rebuild)
        {
            _settings.Update(update);
            UpdatePolicy();
            Changed?.Invoke();
            RefreshTabs();
            if (rebuild)
                _ = RebuildAsync();
        }

        void UpdatePolicy()
        {
            AdBlockSettings settings = _settings.Snapshot;
            _policy = new Policy(settings.Enabled, settings.BypassPrivateNetworks, settings.AllowlistedDomains.ToArray());
        }

        static void RefreshTabs()
        {
            foreach (var window in BrowserApp.Current?.Windows ?? Array.Empty<Views.MainWindow>())
            {
                foreach (var tab in window.Tabs)
                    tab.Engine?.RefreshContentFilter();
            }
        }

        public bool IsSiteAllowed(string? host) => IsAllowed(_policy, AdBlockDomain.NormalizeHost(host));

        static bool IsAllowed(Policy policy, string normalized)
        {
            if (normalized.Length == 0)
                return false;
            foreach (string domain in policy.Allowlist)
            {
                if (AdBlockDomain.IsSameOrSubdomain(normalized, domain))
                    return true;
            }
            return false;
        }

        public void SetSiteAllowed(string? host, bool allowed)
        {
            string normalized = AdBlockDomain.NormalizeHost(host);
            if (normalized.Length == 0)
                return;

            UpdateSettings(settings =>
            {
                settings.AllowlistedDomains.RemoveAll(d => AdBlockDomain.IsSameOrSubdomain(normalized, d) || d.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (allowed)
                    settings.AllowlistedDomains.Add(normalized);
            }, rebuild: false);
        }

        /// <summary>Réseau local (homelab) : jamais filtré si le réglage le demande.</summary>
        public bool IsLocal(string? host)
            => _policy.BypassPrivateNetworks && (AdBlockDomain.IsPrivateOrLocalHost(AdBlockDomain.NormalizeHost(host)) || UrlResolver.IsLocalHost(host));

        /// <summary>Le filtre doit-il s'appliquer à cette page ? (Fil du moteur : lit seulement l'instantané.)</summary>
        public bool ShouldFilter(string? pageUrl)
        {
            Policy policy = _policy;
            if (!policy.Enabled)
                return false;

            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
                return true;

            string host = AdBlockDomain.NormalizeHost(uri.Host);
            if (policy.BypassPrivateNetworks && (AdBlockDomain.IsPrivateOrLocalHost(host) || UrlResolver.IsLocalHost(uri.Host)))
                return false;

            return !IsAllowed(policy, host);
        }

        public async Task<AdBlockUpdateResult> UpdateListsAsync(bool force)
        {
            Status = Tr("Mise à jour des listes…");
            Changed?.Invoke();

            AdBlockUpdateResult result;
            try
            {
                if (FiltersRequests)
                {
                    result = await Task.Run(() => _module.UpdateListsAsync(force));
                    UpdateStatus();
                    return result;
                }
                result = await Task.Run(() => _lists.UpdateAsync(force));
            }
            catch (Exception ex)
            {
                result = new AdBlockUpdateResult(false, 0, ex.Message);
            }

            if (result.UpdatedListCount > 0)
                await RebuildAsync();
            else
                UpdateStatus();

            return result;
        }

        /// <summary>Recompile les règles si les listes ou les réglages ont changé.</summary>
        public async Task RebuildAsync()
        {
            if (!IsSupported)
                return;
            if (FiltersRequests)
            {
                await _module.ReloadForCurrentProfileAsync();
                UpdateStatus();
                return;
            }
            if (_building)
            {
                _buildAgain = true;
                return;
            }

            _building = true;
            try
            {
                do
                {
                    _buildAgain = false;
                    await BuildOnceAsync();
                }
                while (_buildAgain);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Anti-pub] " + ex);
                Status = Tr("Bloqueur indisponible : {0}", ex.Message);
            }
            finally
            {
                _building = false;
                Changed?.Invoke();
            }
        }

        async Task BuildOnceAsync()
        {
            AdBlockSettings settings = _settings.Current;
            IReadOnlyList<(string SourceName, string Content)> sources = await Task.Run(() => _lists.LoadAvailableLists());
            string id = IdentifierPrefix + ComputeKey(sources, settings.CosmeticFiltering);
            if (id == _filterId)
                return;

            FilterState? state = ReadState();
            nint filter = 0;
            int ruleCount = 0;

            if (state?.Id == id)
            {
                filter = await EngineHost.LoadContentFilterAsync(_storeDirectory, id);
                ruleCount = state.RuleCount;
            }

            if (filter == 0)
            {
                Status = Tr("Préparation du bloqueur de publicités…");
                Changed?.Invoke();

                ContentRuleSet rules = await Task.Run(() => ContentRuleConverter.Convert(sources, settings.CosmeticFiltering));
                filter = await EngineHost.CompileContentFilterAsync(_storeDirectory, id, rules.Json);
                ruleCount = rules.RuleCount;
                WriteState(new FilterState(id, ruleCount));
                RuntimeLogBuffer.Append($"[Anti-pub] {rules.RuleCount} règles ({rules.NetworkFilters} réseau, {rules.CosmeticFilters} masquage, {rules.SkippedFilters} ignorées)");
            }

            _filterId = id;
            RuleCount = ruleCount;
            IsReady = true;
            // Les onglets reprennent le nouveau filtre ; l'ancien est libéré par le moteur.
            EngineHost.SetContentFilter(filter);
            UpdateStatus();
        }

        void UpdateStatus()
        {
            if (FiltersRequests)
            {
                RuleCount = _module.Engine.NetworkRuleCount + _module.Engine.CosmeticRuleCount;
                IsReady = _module.Engine.NetworkRuleCount > 0;
            }
            Status = !Settings.Enabled
                ? Tr("Bloqueur désactivé")
                : IsReady
                    ? Tr("{0} règles actives", RuleCount.ToString("N0", Loc.Culture))
                    : Tr("Préparation du bloqueur de publicités…");
            Changed?.Invoke();
        }

        static string ComputeKey(IReadOnlyList<(string SourceName, string Content)> sources, bool cosmetic)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"v{ContentRuleConverter.FormatVersion};cosmetic={cosmetic};"));
            foreach ((string name, string content) in sources)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
                hash.AppendData(Encoding.UTF8.GetBytes(content));
            }
            return Convert.ToHexString(hash.GetHashAndReset())[..24].ToLowerInvariant();
        }

        sealed record FilterState(string Id, int RuleCount);

        FilterState? ReadState()
        {
            try
            {
                return File.Exists(_statePath) ? JsonSerializer.Deserialize<FilterState>(File.ReadAllText(_statePath)) : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        void WriteState(FilterState state)
        {
            try
            {
                Directory.CreateDirectory(_storeDirectory);
                AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Lignes de la page de diagnostic.</summary>
        public IReadOnlyList<(string, string)> DiagnosticRows()
        {
            AdBlockSettings settings = Settings;
            return new List<(string, string)>
            {
                (Tr("État"), Status),
                (Tr("Listes"), string.Join(", ", settings.Subscriptions.Where(s => s.Enabled).Select(s => s.Name))),
                (Tr("Dernière mise à jour"), settings.LastSuccessfulUpdateUtc?.ToLocalTime().ToString("g", Loc.Culture) ?? Tr("jamais")),
                (Tr("Sites autorisés"), settings.AllowlistedDomains.Count.ToString(Loc.Culture))
            };
        }

        public void Dispose() => _module.Dispose();
    }
}
