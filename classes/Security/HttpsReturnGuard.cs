using System;

namespace MyHomelabBrowser.classes.Security
{
    /// <summary>
    /// Page passée en HTTPS par le navigateur (voir <see cref="HttpsUpgradePolicy"/>) qui repart
    /// d'elle-même vers http:// : script ou balise de la page, que le navigateur ne voit pas comme une
    /// redirection du serveur. Le site veut HTTP ; sans cela, chaque retour serait de nouveau passé
    /// en HTTPS, sans fin. Deux signes : un retour en http:// du même site peu après son ouverture en
    /// HTTPS, ou le même site ouvert en HTTPS trois fois en peu de temps (retour par un autre chemin).
    /// Les instants sont en millisecondes (Environment.TickCount64).
    /// </summary>
    public sealed class HttpsReturnGuard
    {
        public const long ReturnDelay = 15_000;
        public const long LoopPeriod = 20_000;
        public const int LoopCount = 3;

        string? _openedHost;
        long _openedAt;
        string? _loopHost;
        long _loopStart;
        int _loops;

        /// <summary>
        /// Page du site <paramref name="host"/> ouverte en HTTPS après passage par le navigateur.
        /// Vrai : allers-retours répétés, le site doit rester en HTTP.
        /// </summary>
        public bool UpgradedPageOpened(string host, long now)
        {
            _openedHost = host;
            _openedAt = now;
            if (!string.Equals(_loopHost, host, StringComparison.OrdinalIgnoreCase) || now - _loopStart > LoopPeriod)
            {
                _loopHost = host;
                _loopStart = now;
                _loops = 0;
            }
            return ++_loops >= LoopCount;
        }

        /// <summary>Navigation qui commence vers <paramref name="target"/>. Vrai : le site revient de lui-même en HTTP.</summary>
        public bool ReturnsToHttp(Uri target, long now)
        {
            if (target.Scheme != Uri.UriSchemeHttp || _openedHost == null ||
                !string.Equals(target.IdnHost, _openedHost, StringComparison.OrdinalIgnoreCase) || now - _openedAt >= ReturnDelay)
                return false;
            _openedHost = null;
            return true;
        }
    }
}
