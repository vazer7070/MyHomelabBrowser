using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PommeBrowser.Linux.Core
{
    public sealed record ReleaseAsset(string Name, string Url, long Size);

    public sealed record ReleaseInfo(string Tag, Version Version, IReadOnlyList<ReleaseAsset> Assets, string? PageUrl);

    /// <summary>
    /// Mise à jour de l'AppImage depuis les versions publiées sur GitHub (même dépôt que l'édition
    /// Windows : le tag est le numéro de version, l'AppImage et son empreinte SHA-256 sont
    /// jointes à la version). Logique sans réseau ni interface, testée à part.
    /// </summary>
    public static class AppImageUpdate
    {
        public const string Repository = "vazer7070/PommeBrowser-release";

        /// <summary>Une AppImage fait environ 90 Mo : au-delà de cette taille, le fichier est refusé.</summary>
        public const long MaxSize = 512L * 1024 * 1024;

        /// <summary>POMMEBROWSER_UPDATE_URL permet de tester avec un autre serveur.</summary>
        public static string LatestReleaseUrl
            => Environment.GetEnvironmentVariable("POMMEBROWSER_UPDATE_URL") is { Length: > 0 } url
                ? url
                : $"https://api.github.com/repos/{Repository}/releases/latest";

        public static string? CurrentArchitecture => RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => null
        };

        public static string AssetName(Version version, string architecture)
            => $"PommeBrowser-{version.ToString(3)}-{architecture}.AppImage";

        /// <summary>« 0.9.9 » ou « v0.9.9 » ; les préversions (0.9.9-beta.1) ne sont pas proposées.</summary>
        public static bool TryParseVersion(string? tag, out Version version)
        {
            version = new Version(0, 0);
            string value = (tag ?? string.Empty).Trim().TrimStart('v', 'V');
            if (value.Length == 0 || value.Contains('-') || value.Contains('+'))
                return false;
            if (!Version.TryParse(value, out Version? parsed) || parsed.Major < 0)
                return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }

        public static bool IsNewer(Version candidate, Version? current)
        {
            if (current == null)
                return false;
            var normalized = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            return candidate > normalized;
        }

        /// <summary>Réponse de l'API GitHub (/releases/latest) ; null pour un brouillon, une préversion ou une réponse inattendue.</summary>
        public static ReleaseInfo? ParseRelease(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    Bool(root, "draft") || Bool(root, "prerelease") ||
                    !root.TryGetProperty("tag_name", out JsonElement tag) ||
                    !TryParseVersion(tag.GetString(), out Version version))
                {
                    return null;
                }

                var assets = new List<ReleaseAsset>();
                if (root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in list.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                        string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                        long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long value) ? value : 0;
                        if (!string.IsNullOrEmpty(name) && IsHttps(url))
                            assets.Add(new ReleaseAsset(name, url!, size));
                    }
                }

                string? page = root.TryGetProperty("html_url", out JsonElement html) && IsHttps(html.GetString()) ? html.GetString() : null;
                return new ReleaseInfo(tag.GetString()!, version, assets, page);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>L'AppImage de cette architecture et son fichier d'empreinte, s'ils sont publiés.</summary>
        public static (ReleaseAsset Image, ReleaseAsset Checksum)? FindAssets(ReleaseInfo release, string architecture)
        {
            string name = AssetName(release.Version, architecture);
            ReleaseAsset? image = release.Assets.FirstOrDefault(a => a.Name == name);
            ReleaseAsset? checksum = release.Assets.FirstOrDefault(a => a.Name == name + ".sha256");
            if (image == null || checksum == null || image.Size <= 0 || image.Size > MaxSize)
                return null;
            return (image, checksum);
        }

        /// <summary>Format de sha256sum : « empreinte  nom ». L'empreinte doit viser ce fichier.</summary>
        public static string? ParseChecksum(string content, string fileName)
        {
            foreach (string raw in content.Split('\n'))
            {
                string[] parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                    continue;
                string hash = parts[0].ToLowerInvariant();
                string name = parts[1].TrimStart('*').Trim();
                if (name == fileName && hash.Length == 64 && hash.All(Uri.IsHexDigit))
                    return hash;
            }
            return null;
        }

        /// <summary>Exécutable ELF portant la signature des AppImage de type 2 (« AI\x02 » à l'octet 8).</summary>
        public static bool LooksLikeAppImage(ReadOnlySpan<byte> header)
            => header.Length >= 11 &&
               header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' &&
               header[8] == (byte)'A' && header[9] == (byte)'I' && header[10] == 0x02;

        /// <summary>Le fichier de l'AppImage peut être remplacé (dossier accessible en écriture).</summary>
        public static bool CanReplace(string? appImage)
        {
            if (string.IsNullOrEmpty(appImage) || !File.Exists(appImage))
                return false;
            string? directory = Path.GetDirectoryName(appImage);
            if (directory == null)
                return false;
            try
            {
                string probe = Path.Combine(directory, ".pommebrowser-write-test-" + Environment.ProcessId);
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        static bool Bool(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        static bool IsHttps(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;
    }
}
