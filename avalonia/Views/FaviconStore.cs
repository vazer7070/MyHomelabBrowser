using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Icônes des sites : en mémoire, et sur le disque dans le dossier du profil (favicons/&lt;hôte&gt;.png,
    /// comme l'édition Windows) pour les tuiles de la page d'accueil et la barre de favoris.
    /// Les onglets privés n'y écrivent rien.
    /// </summary>
    public static class FaviconStore
    {
        const int DecodeSize = 32;
        const int MaxBytes = 512 * 1024;

        static readonly Dictionary<string, Bitmap?> Memory = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? FaviconUpdated;

        static string Directory => Path.Combine(AppDataContext.Root, "favicons");

        public static string NormalizeHost(string? host)
        {
            string value = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            return value.StartsWith("www.", StringComparison.Ordinal) ? value[4..] : value;
        }

        public static string HostFromUrl(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? NormalizeHost(uri.Host) : string.Empty;

        /// <summary>Changement de profil : les icônes gardées en mémoire sont celles du profil quitté.</summary>
        public static void ForgetMemory() => Memory.Clear();

        public static Bitmap? TryGet(string? url)
        {
            string host = HostFromUrl(url);
            if (host.Length == 0)
                return null;
            if (Memory.TryGetValue(host, out Bitmap? cached))
                return cached;

            Bitmap? image = null;
            try
            {
                string path = PathFor(host);
                if (File.Exists(path))
                    image = Decode(File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cache illisible : pas d'icône.
            }
            Memory[host] = image;
            return image;
        }

        /// <summary>Icône reçue pour une page (fil de l'interface) : gardée pour son site.</summary>
        public static void Save(string? url, byte[] png)
        {
            string host = HostFromUrl(url);
            if (host.Length == 0 || png.Length == 0 || png.Length > MaxBytes || Decode(png) is not { } image)
                return;

            Memory[host] = image;
            FaviconUpdated?.Invoke(host);
            string path = PathFor(host);
            _ = Task.Run(() =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    AtomicFile.WriteAllBytes(path, png);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Le cache disque est un confort : la mémoire suffit pour la session.
                }
            });
        }

        public static Bitmap? Decode(byte[]? png)
        {
            if (png is not { Length: > 0 })
                return null;
            try
            {
                using var stream = new MemoryStream(png, writable: false);
                return Bitmap.DecodeToWidth(stream, DecodeSize);
            }
            catch (Exception)
            {
                // Image illisible : pas d'icône.
                return null;
            }
        }

        static string PathFor(string host)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var safe = new char[host.Length];
            for (int i = 0; i < host.Length; i++)
                safe[i] = Array.IndexOf(invalid, host[i]) >= 0 ? '_' : host[i];
            return Path.Combine(Directory, new string(safe) + ".png");
        }
    }
}
