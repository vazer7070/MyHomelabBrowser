using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;
using MyHomelabBrowser.classes.Localization;
using PommeBrowser.Linux.Core;
using PommeBrowser.Linux.Ui;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Web
{
    /// <summary>Filtre appliqué à un onglet (identifiant WebKit, ou null).</summary>
    sealed class TabFilterState
    {
        public string? AppliedId { get; set; }
    }

    /// <summary>
    /// Bloqueur de publicités et de pisteurs. Mêmes listes et mêmes réglages que l'édition
    /// Windows (dossier AdBlock du profil) ; les listes sont converties en règles WebKit
    /// (ContentRuleConverter), compilées une fois puis gardées en cache.
    /// Un site autorisé, ou du réseau local, est simplement affiché sans le filtre.
    /// </summary>
    sealed class AdBlocker : IDisposable
    {
        const string IdentifierPrefix = "pommebrowser-";

        readonly AdBlockSettingsService _settings = new();
        readonly AdBlockFilterListService _lists;
        readonly WebKit.UserContentFilterStore _store;
        readonly string _statePath;

        IntPtr _filter;
        string? _filterId;
        bool _building;
        bool _buildAgain;

        public AdBlocker()
        {
            _lists = new AdBlockFilterListService(_settings);
            string storeDirectory = LinuxPaths.Cache("content-filters");
            Directory.CreateDirectory(storeDirectory);
            _store = WebKit.UserContentFilterStore.New(storeDirectory);
            _statePath = Path.Combine(storeDirectory, "state.json");
        }

        /// <summary>Filtre remplacé, réglages ou état modifiés (toujours sur le fil de l'interface).</summary>
        public event Action? Changed;

        public AdBlockSettings Settings => _settings.Snapshot;

        public string Status { get; private set; } = Tr("Préparation du bloqueur de publicités…");

        public int RuleCount { get; private set; }

        public bool IsReady => _filter != IntPtr.Zero;

        public async Task StartAsync()
        {
            await RebuildAsync();
            if (_lists.NeedsUpdate(_settings.Current))
                await UpdateListsAsync(force: false);
        }

        public void UpdateSettings(Action<AdBlockSettings> update, bool rebuild)
        {
            _settings.Update(update);
            Changed?.Invoke();
            if (rebuild)
                _ = RebuildAsync();
        }

        public bool IsSiteAllowed(string? host)
        {
            string normalized = AdBlockDomain.NormalizeHost(host);
            if (normalized.Length == 0)
                return false;

            foreach (string domain in Settings.AllowlistedDomains)
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

        /// <summary>Le filtre doit-il s'appliquer à cette page ?</summary>
        public bool ShouldFilter(string? pageUrl)
        {
            AdBlockSettings settings = Settings;
            if (!settings.Enabled || _filter == IntPtr.Zero)
                return false;

            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
                return true;

            string host = AdBlockDomain.NormalizeHost(uri.Host);
            if (settings.BypassPrivateNetworks && (AdBlockDomain.IsPrivateOrLocalHost(host) || UrlResolver.IsLocalHost(uri.Host)))
                return false;

            return !IsSiteAllowed(host);
        }

        /// <summary>Ajoute ou retire le filtre d'un onglet selon la page qui va s'afficher.</summary>
        public void ApplyTo(WebKit.UserContentManager manager, TabFilterState state, string? pageUrl)
        {
            string? wanted = ShouldFilter(pageUrl) ? _filterId : null;
            if (state.AppliedId == wanted)
                return;

            if (state.AppliedId != null)
                manager.RemoveFilterById(state.AppliedId);
            if (wanted != null)
                Native.webkit_user_content_manager_add_filter(Native.Pointer(manager), _filter);
            state.AppliedId = wanted;
        }

        public async Task<AdBlockUpdateResult> UpdateListsAsync(bool force)
        {
            Status = Tr("Mise à jour des listes…");
            Changed?.Invoke();

            AdBlockUpdateResult result;
            try
            {
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
            IntPtr filter = IntPtr.Zero;
            int ruleCount = 0;

            if (state?.Id == id)
            {
                filter = await TryLoadAsync(id);
                ruleCount = state.RuleCount;
            }

            if (filter == IntPtr.Zero)
            {
                Status = Tr("Préparation du bloqueur de publicités…");
                Changed?.Invoke();

                ContentRuleSet rules = await Task.Run(() => ContentRuleConverter.Convert(sources, settings.CosmeticFiltering));
                filter = await SaveAsync(id, rules.Json);
                ruleCount = rules.RuleCount;
                WriteState(new FilterState(id, ruleCount));
                RuntimeLogBuffer.Append($"[Anti-pub] {rules.RuleCount} règles ({rules.NetworkFilters} réseau, {rules.CosmeticFilters} masquage, {rules.SkippedFilters} ignorées)");

                if (state?.Id is { } previous && previous != id)
                    _ = RemoveAsync(previous);
            }

            IntPtr old = _filter;
            _filter = filter;
            _filterId = id;
            RuleCount = ruleCount;
            UpdateStatus();

            // Les onglets remplacent l'ancien filtre (Changed), puis il est libéré.
            Changed?.Invoke();
            if (old != IntPtr.Zero)
                Native.webkit_user_content_filter_unref(old);
        }

        void UpdateStatus()
        {
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

        Task<IntPtr> TryLoadAsync(string id)
            => Native.RunAsync(
                callback => Native.webkit_user_content_filter_store_load(Native.Pointer(_store), id, IntPtr.Zero, callback, IntPtr.Zero),
                result =>
                {
                    IntPtr filter = Native.webkit_user_content_filter_store_load_finish(Native.Pointer(_store), result, out IntPtr error);
                    if (error != IntPtr.Zero)
                        Native.g_error_free(error);
                    return filter;
                });

        Task<IntPtr> SaveAsync(string id, string json)
        {
            IntPtr bytes = Native.NewBytes(Encoding.UTF8.GetBytes(json));
            return Native.RunAsync(
                callback =>
                {
                    Native.webkit_user_content_filter_store_save(Native.Pointer(_store), id, bytes, IntPtr.Zero, callback, IntPtr.Zero);
                    Native.g_bytes_unref(bytes);
                },
                result =>
                {
                    IntPtr filter = Native.webkit_user_content_filter_store_save_finish(Native.Pointer(_store), result, out IntPtr error);
                    Native.ThrowIfError(error);
                    return filter;
                });
        }

        Task<bool> RemoveAsync(string id)
            => Native.RunAsync(
                callback => Native.webkit_user_content_filter_store_remove(Native.Pointer(_store), id, IntPtr.Zero, callback, IntPtr.Zero),
                result =>
                {
                    bool removed = Native.webkit_user_content_filter_store_remove_finish(Native.Pointer(_store), result, out IntPtr error);
                    if (error != IntPtr.Zero)
                        Native.g_error_free(error);
                    return removed;
                });

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
                AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        public void Dispose()
        {
            _lists.Dispose();
            if (_filter != IntPtr.Zero)
            {
                Native.webkit_user_content_filter_unref(_filter);
                _filter = IntPtr.Zero;
            }
        }
    }
}
