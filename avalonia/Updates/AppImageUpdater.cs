using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Updates
{
    /// <summary>
    /// Mise à jour automatique de l'AppImage : la nouvelle version est téléchargée à côté de
    /// l'ancienne, vérifiée (taille, empreinte SHA-256 publiée avec la version, format AppImage),
    /// puis remplace le fichier d'un seul coup. Elle s'applique au redémarrage suivant.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    sealed class AppImageUpdater : IUpdater
    {
        readonly HttpClient _http;
        bool _busy;

        public AppImageUpdater()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PommeBrowser/" + Current.ToString(3) + " (Linux)");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public static Version Current { get; } = typeof(AppImageUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

        /// <summary>Chemin de l'AppImage en cours d'exécution (null depuis les sources).</summary>
        public static string? AppImagePath
            => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path && File.Exists(path) ? path : null;

        public string Status { get; private set; } = string.Empty;
        public bool IsBusy => _busy;

        /// <summary>Version plus récente publiée mais pas installée (pas une AppImage, dossier protégé…).</summary>
        public ReleaseInfo? Available { get; private set; }

        /// <summary>Version installée, prise en compte au redémarrage.</summary>
        public Version? Installed { get; private set; }

        public event Action? Changed;

        public bool CanUpdate => AppImagePath != null;

        public string? ReleasePage => Available?.PageUrl;

        public async Task CheckAsync()
        {
            if (_busy || Installed != null)
                return;

            _busy = true;
            SetStatus(Tr("Recherche de mises à jour…"));
            string? temporary = null;
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string json = await _http.GetStringAsync(AppImageUpdate.LatestReleaseUrl, cancel.Token);
                ReleaseInfo? release = AppImageUpdate.ParseRelease(json);
                if (release == null || !AppImageUpdate.IsNewer(release.Version, Current))
                {
                    Available = null;
                    SetStatus(Tr("PommeBrowser est à jour (version {0}).", Current.ToString(3)));
                    return;
                }

                Available = release;
                string? appImage = AppImagePath;
                string? architecture = AppImageUpdate.CurrentArchitecture;
                if (appImage == null || architecture == null)
                {
                    SetStatus(Tr("La version {0} est disponible. La mise à jour automatique ne concerne que l'AppImage.", release.Version.ToString(3)));
                    return;
                }

                if (AppImageUpdate.FindAssets(release, architecture) is not { } assets)
                {
                    SetStatus(Tr("La version {0} est publiée, mais pas encore en AppImage pour cet ordinateur.", release.Version.ToString(3)));
                    return;
                }

                if (!AppImageUpdate.CanReplace(appImage))
                {
                    SetStatus(Tr("La version {0} est disponible, mais le dossier de l'AppImage n'est pas modifiable : téléchargez-la depuis la page de la version.", release.Version.ToString(3)));
                    return;
                }

                SetStatus(Tr("Téléchargement de la version {0}…", release.Version.ToString(3)));
                string checksumText = await _http.GetStringAsync(assets.Checksum.Url);
                string expected = AppImageUpdate.ParseChecksum(checksumText, assets.Image.Name)
                    ?? throw new InvalidDataException(Tr("Empreinte SHA-256 absente ou illisible."));

                temporary = Path.Combine(Path.GetDirectoryName(appImage)!, "." + assets.Image.Name + ".part");
                string actual = await DownloadAsync(assets.Image, temporary);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Tr("L'empreinte SHA-256 du fichier téléchargé ne correspond pas : fichier écarté."));

                File.SetUnixFileMode(temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                // Remplacement en une opération : l'AppImage en cours d'exécution garde l'ancienne version ouverte.
                File.Move(temporary, appImage, overwrite: true);
                temporary = null;
                Installed = release.Version;
                Available = null;
                SetStatus(Tr("La version {0} est installée : elle s'appliquera au prochain démarrage.", release.Version.ToString(3)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                RuntimeLogBuffer.Append("[Mise à jour] " + ex.Message);
                SetStatus(ex is InvalidDataException
                    ? ex.Message
                    : Tr("Impossible de vérifier les mises à jour : {0}", ex.Message));
            }
            finally
            {
                if (temporary != null)
                    TryDelete(temporary);
                _busy = false;
                Changed?.Invoke();
            }
        }

        /// <summary>Téléchargement limité à la taille annoncée, empreinte calculée au fil de l'eau.</summary>
        async Task<string> DownloadAsync(ReleaseAsset asset, string destination)
        {
            using HttpResponseMessage response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != asset.Size)
                throw new InvalidDataException(Tr("Taille du fichier téléchargé inattendue : fichier écarté."));

            await using Stream source = await response.Content.ReadAsStreamAsync();
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[1 << 16];
            byte[] header = new byte[16];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                if (total < header.Length)
                    Array.Copy(buffer, 0, header, total, (int)Math.Min(read, header.Length - total));
                total += read;
                if (total > asset.Size)
                    throw new InvalidDataException(Tr("Taille du fichier téléchargé inattendue : fichier écarté."));
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read));
            }

            if (total != asset.Size)
                throw new InvalidDataException(Tr("Taille du fichier téléchargé inattendue : fichier écarté."));
            if (!AppImageUpdate.LooksLikeAppImage(header))
                throw new InvalidDataException(Tr("Le fichier téléchargé n'est pas une AppImage : fichier écarté."));

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        void SetStatus(string status)
        {
            Status = status;
            Changed?.Invoke();
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
