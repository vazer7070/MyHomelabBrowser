using System;
using System.Collections.Generic;
using System.Linq;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Page qui se recharge sans fin (koramgame.com, rechargé une douzaine de fois par seconde) :
    /// repérée à <see cref="LoopCount"/> pages ouvertes en moins de <see cref="LoopPeriod"/> ms,
    /// quel que soit le site (une boucle peut passer d'un sous-domaine à l'autre) ; aucun humain ne
    /// navigue aussi vite. Garde le trajet des dernières navigations (adresses sans leurs paramètres,
    /// qui peuvent contenir un jeton de session), et les sites passés en HTTPS par le navigateur
    /// pendant la boucle. Les instants sont en millisecondes (Environment.TickCount64).
    /// </summary>
    public sealed class NavigationLoopGuard
    {
        public const int LoopCount = 8;
        public const long LoopPeriod = 6_000;
        const int MaxSteps = 12;

        readonly Queue<string> _steps = new();
        readonly List<long> _opened = new();
        readonly Dictionary<string, long> _upgraded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Navigation commencée (lien, script, redirection côté page).</summary>
        public void Started(Uri url) => Add("→ " + Short(url));

        /// <summary>Redirection du serveur.</summary>
        public void Redirected(Uri url) => Add("↪ " + Short(url));

        /// <summary>Adresse passée en HTTPS par le navigateur.</summary>
        public void Upgraded(Uri url, long now)
        {
            _upgraded[url.IdnHost] = now;
            Add("⇧ " + Short(url));
        }

        /// <summary>Page ouverte. Vrai : boucle (voir la classe).</summary>
        public bool Opened(Uri url, long now)
        {
            Add("✓ " + Short(url));
            _opened.Add(now);
            _opened.RemoveAll(at => now - at > LoopPeriod);
            return _opened.Count >= LoopCount;
        }

        /// <summary>Sites passés en HTTPS par le navigateur pendant la boucle.</summary>
        public IReadOnlyList<string> UpgradedHosts(long now)
            => _upgraded.Where(entry => now - entry.Value <= LoopPeriod).Select(entry => entry.Key).ToList();

        /// <summary>Dernières navigations, pour le journal.</summary>
        public string Trail => string.Join(" ", _steps);

        /// <summary>Boucle traitée, ou navigation voulue par l'utilisateur : tout repart de zéro.</summary>
        public void Reset()
        {
            _steps.Clear();
            _opened.Clear();
            _upgraded.Clear();
        }

        void Add(string step)
        {
            _steps.Enqueue(step);
            while (_steps.Count > MaxSteps)
                _steps.Dequeue();
        }

        static string Short(Uri url)
        {
            if (url.Scheme is not ("http" or "https"))
                return url.Scheme + ":";
            string path = url.AbsolutePath.Length > 40 ? url.AbsolutePath[..40] + "…" : url.AbsolutePath;
            return url.Scheme + "://" + url.IdnHost + path + (url.Query.Length > 1 ? "?…" : string.Empty);
        }
    }
}
