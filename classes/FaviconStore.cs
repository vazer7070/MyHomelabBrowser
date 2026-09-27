using MyHomelabBrowser.classes.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Cache local des favicons, alimenté par WebView2 pendant la navigation normale.
    /// L'historique et les favoris l'utilisent au lieu d'interroger un service tiers
    /// (auparavant google.com/s2/favicons recevait la liste des sites visités).
    /// Les onglets privés n'y écrivent jamais.
    /// </summary>
    public static class FaviconStore
    {
        private const int DecodeSize = 32;
        private static readonly object Sync = new();
        private static readonly Dictionary<string, ImageSource?> Memory = new(StringComparer.OrdinalIgnoreCase);
        private static string _memoryRoot = string.Empty;

        public static event Action<string>? FaviconUpdated;

        private static string FaviconDirectory => Path.Combine(AppDataContext.Root, "favicons");

        public static string NormalizeHost(string? host)
        {
            string value = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            return value.StartsWith("www.", StringComparison.Ordinal) ? value[4..] : value;
        }

        public static string HostFromUrl(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? NormalizeHost(uri.Host) : string.Empty;

        public static ImageSource? TryGet(string? url)
        {
            string host = HostFromUrl(url);
            if (host.Length == 0)
                return null;

            lock (Sync)
            {
                ResetIfProfileChanged();
                if (Memory.TryGetValue(host, out ImageSource? cached))
                    return cached;
            }

            ImageSource? image = LoadFromDisk(host);

            lock (Sync)
                Memory[host] = image;

            return image;
        }

        public static async Task SaveAsync(string? url, Stream pngStream)
        {
            string host = HostFromUrl(url);
            if (host.Length == 0 || pngStream == null)
                return;

            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                await pngStream.CopyToAsync(buffer).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }

            if (bytes.Length == 0 || bytes.Length > 512 * 1024)
                return;

            ImageSource? image = Decode(bytes);
            if (image == null)
                return;

            lock (Sync)
            {
                ResetIfProfileChanged();
                Memory[host] = image;
            }

            try
            {
                string path = GetPath(host);
                await Task.Run(() => AtomicFile.WriteAllBytes(path, bytes)).ConfigureAwait(false);
            }
            catch
            {
                // Le cache disque est un confort : la mémoire suffit pour la session.
            }

            FaviconUpdated?.Invoke(host);
        }

        public static ImageSource? Decode(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = DecodeSize;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource? LoadFromDisk(string host)
        {
            try
            {
                string path = GetPath(host);
                return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetPath(string host)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var safe = new char[host.Length];
            for (int i = 0; i < host.Length; i++)
                safe[i] = Array.IndexOf(invalid, host[i]) >= 0 ? '_' : host[i];

            return Path.Combine(FaviconDirectory, new string(safe) + ".png");
        }

        private static void ResetIfProfileChanged()
        {
            if (string.Equals(_memoryRoot, AppDataContext.Root, StringComparison.OrdinalIgnoreCase))
                return;

            Memory.Clear();
            _memoryRoot = AppDataContext.Root;
        }
    }
}
