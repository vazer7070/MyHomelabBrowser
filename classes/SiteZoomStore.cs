using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Niveau de zoom mémorisé par site (hôte et port : deux services du même NAS
    /// sur des ports différents gardent chacun leur zoom).
    /// </summary>
    public sealed class SiteZoomStore
    {
        /// <summary>
        /// Paliers identiques à ceux de Chrome et Edge.
        /// </summary>
        public static readonly double[] Levels =
        {
            0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0
        };

        public const double MinimumZoom = 0.25;
        public const double MaximumZoom = 5.0;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly Func<string> _pathProvider;
        private readonly object _gate = new();
        private Dictionary<string, double> _zooms = new(StringComparer.OrdinalIgnoreCase);
        private string? _loadedPath;

        public SiteZoomStore(Func<string> pathProvider)
        {
            _pathProvider = pathProvider;
        }

        /// <summary>
        /// Clé de site, ou null pour les adresses dont le zoom n'est pas mémorisé (about:, file:…).
        /// </summary>
        public static string? KeyFor(Uri? uri)
        {
            if (uri == null || !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return null;

            return uri.IsDefaultPort
                ? uri.Host.ToLowerInvariant()
                : uri.Host.ToLowerInvariant() + ":" + uri.Port;
        }

        public double Get(Uri? uri)
        {
            string? key = KeyFor(uri);
            if (key == null)
                return 1.0;

            lock (_gate)
            {
                EnsureLoaded();
                return _zooms.TryGetValue(key, out double zoom) ? zoom : 1.0;
            }
        }

        public void Set(Uri? uri, double zoom)
        {
            string? key = KeyFor(uri);
            if (key == null)
                return;

            zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);

            lock (_gate)
            {
                EnsureLoaded();

                bool isDefault = Math.Abs(zoom - 1.0) < 0.005;
                bool changed = isDefault
                    ? _zooms.Remove(key)
                    : !_zooms.TryGetValue(key, out double previous) || Math.Abs(previous - zoom) >= 0.005;

                if (!isDefault)
                    _zooms[key] = Math.Round(zoom, 3);

                if (changed)
                    Save();
            }
        }

        /// <summary>
        /// Palier suivant (direction &gt; 0) ou précédent à partir d'un zoom quelconque.
        /// </summary>
        public static double Step(double current, int direction)
        {
            if (direction > 0)
            {
                foreach (double level in Levels)
                {
                    if (level > current + 0.005)
                        return level;
                }
                return MaximumZoom;
            }

            for (int i = Levels.Length - 1; i >= 0; i--)
            {
                if (Levels[i] < current - 0.005)
                    return Levels[i];
            }
            return MinimumZoom;
        }

        public static string Format(double zoom) => $"{Math.Round(zoom * 100):0} %";

        private void EnsureLoaded()
        {
            string path = _pathProvider();
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
                return;

            _loadedPath = path;
            _zooms = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(path))
                    return;

                var loaded = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path));
                if (loaded == null)
                    return;

                foreach (var (key, value) in loaded.Where(pair => pair.Value is >= MinimumZoom and <= MaximumZoom))
                    _zooms[key] = value;
            }
            catch
            {
                // Fichier illisible : on repart des zooms par défaut.
            }
        }

        private void Save()
        {
            if (_loadedPath == null)
                return;

            try
            {
                AtomicFile.WriteAllText(_loadedPath, JsonSerializer.Serialize(_zooms, JsonOptions));
            }
            catch
            {
                // Un zoom non mémorisé n'est pas bloquant.
            }
        }
    }
}
