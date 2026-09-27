using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Security
{
    /// <summary>Décision mémorisée pour un site : une autorisation (caméra…) ou l'accès en HTTP.</summary>
    public sealed record SiteDecision(string Site, string Kind, bool Allowed, DateTime DecidedAt);

    /// <summary>
    /// Choix de l'utilisateur par site, enregistrés dans le profil (site-permissions.json) :
    /// autorisations demandées par les pages et sites autorisés à rester en HTTP.
    /// </summary>
    public sealed class SiteSecurityStore
    {
        /// <summary>Type de décision : le site a été ouvert en HTTP malgré l'échec de HTTPS.</summary>
        public const string InsecureHttp = "InsecureHttp";

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>Choix du profil courant (rechargés au changement de profil).</summary>
        public static SiteSecurityStore Current { get; } =
            new(() => Path.Combine(Profiles.AppDataContext.Root, "site-permissions.json"));

        readonly Func<string> _pathProvider;
        readonly object _sync = new();
        List<SiteDecision> _decisions = new();

        public SiteSecurityStore(Func<string> pathProvider)
        {
            _pathProvider = pathProvider;
            Reload();
        }

        public event Action? Changed;

        public IReadOnlyList<SiteDecision> All
        {
            get
            {
                lock (_sync)
                    return _decisions.OrderBy(d => d.Site, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Kind).ToList();
            }
        }

        public void Reload()
        {
            lock (_sync)
            {
                try
                {
                    string path = _pathProvider();
                    _decisions = File.Exists(path)
                        ? JsonSerializer.Deserialize<List<SiteDecision>>(File.ReadAllText(path), JsonOptions) ?? new()
                        : new();
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    _decisions = new();
                }
            }
        }

        public bool? Get(string site, string kind)
        {
            lock (_sync)
                return _decisions.FirstOrDefault(d => Matches(d, site, kind))?.Allowed;
        }

        public bool IsHttpAllowed(string host) => Get(host, InsecureHttp) == true;

        public void Set(string site, string kind, bool allowed)
        {
            lock (_sync)
            {
                _decisions.RemoveAll(d => Matches(d, site, kind));
                _decisions.Add(new SiteDecision(site.ToLowerInvariant(), kind, allowed, DateTime.Now));
                Save();
            }
            Changed?.Invoke();
        }

        public void Remove(SiteDecision decision)
        {
            lock (_sync)
            {
                _decisions.RemoveAll(d => Matches(d, decision.Site, decision.Kind));
                Save();
            }
            Changed?.Invoke();
        }

        public void Clear()
        {
            lock (_sync)
            {
                _decisions.Clear();
                Save();
            }
            Changed?.Invoke();
        }

        static bool Matches(SiteDecision decision, string site, string kind)
            => string.Equals(decision.Site, site, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(decision.Kind, kind, StringComparison.Ordinal);

        void Save()
        {
            try
            {
                AtomicFile.WriteAllText(_pathProvider(), JsonSerializer.Serialize(_decisions, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
