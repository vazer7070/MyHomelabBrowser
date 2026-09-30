using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Basilisk pour Linux (archive officielle ou paquet de la distribution) et module Flash
    /// d'origine (libflashplayer.so). PommeBrowser ne fournit ni l'un ni l'autre : il les
    /// détecte, ou utilise le chemin choisi dans les préférences.
    /// </summary>
    public static class BasiliskInstall
    {
        public const string FlashPluginFile = "libflashplayer.so";

        /// <summary>Emplacements habituels : archive décompressée, /opt, paquets, puis le PATH.</summary>
        public static IEnumerable<string> Candidates(string home, string? pathVariable)
        {
            yield return Path.Combine(home, ".local", "share", "basilisk", "basilisk");
            yield return Path.Combine(home, ".local", "opt", "basilisk", "basilisk");
            yield return Path.Combine(home, "Applications", "basilisk", "basilisk");
            yield return Path.Combine(home, "basilisk", "basilisk");
            yield return "/opt/basilisk/basilisk";
            yield return "/usr/lib/basilisk/basilisk";
            yield return "/usr/lib64/basilisk/basilisk";
            yield return "/usr/local/lib/basilisk/basilisk";

            foreach (string directory in (pathVariable ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Path.IsPathRooted(directory))
                    yield return Path.Combine(directory, "basilisk");
            }
        }

        public static string? Detect(string home, string? pathVariable)
            => Candidates(home, pathVariable).FirstOrDefault(IsLaunchable);

        /// <summary>Fichier exécutable (bit x pour l'utilisateur).</summary>
        public static bool IsLaunchable(string? path)
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
                return false;
            try
            {
                return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Nom et version lus dans application.ini, à côté de l'exécutable réel (lien de /usr/bin suivi).</summary>
        public static (string? Name, string? Version) Describe(string? path)
        {
            if (!IsLaunchable(path))
                return (null, null);

            try
            {
                string real = new FileInfo(path!).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path!;
                string ini = Path.Combine(Path.GetDirectoryName(real)!, "application.ini");
                return File.Exists(ini) ? ParseApplicationIni(File.ReadAllLines(ini)) : (null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, null);
            }
        }

        public static (string? Name, string? Version) ParseApplicationIni(IEnumerable<string> lines)
        {
            string? section = null, name = null, version = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1];
                    continue;
                }
                int equals = line.IndexOf('=');
                if (section != "App" || equals <= 0)
                    continue;
                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                if (key == "Name")
                    name = value;
                else if (key == "Version")
                    version = value;
            }
            return (name, version);
        }

        /// <summary>Dossiers où Basilisk cherche le module Flash (celui de PommeBrowser d'abord).</summary>
        public static IEnumerable<string> PluginDirectories(string home, string pommeDirectory)
        {
            yield return pommeDirectory;
            yield return Path.Combine(home, ".mozilla", "plugins");
            yield return "/usr/lib/mozilla/plugins";
            yield return "/usr/lib64/mozilla/plugins";
        }

        public static string? FindFlashPlugin(string home, string pommeDirectory)
            => PluginDirectories(home, pommeDirectory)
                .Select(d => Path.Combine(d, FlashPluginFile))
                .FirstOrDefault(File.Exists);

        public static IReadOnlyList<string> Arguments(string profileDirectory, Uri url)
            => new[] { "-new-instance", "-no-remote", "-profile", profileDirectory, url.AbsoluteUri };

        /// <summary>
        /// Ni rapport de plantage ni instance partagée ; module Flash cherché aussi dans le dossier
        /// de PommeBrowser ; X11 imposé (Basilisk ne gère pas Wayland, il passe par XWayland).
        /// </summary>
        public static IReadOnlyDictionary<string, string> Environment(string pluginDirectory, string? existingPluginPath)
            => new Dictionary<string, string>
            {
                ["MOZ_CRASHREPORTER_DISABLE"] = "1",
                ["MOZ_CRASHREPORTER_NO_REPORT"] = "1",
                ["MOZ_NO_REMOTE"] = "1",
                ["MOZ_PLUGIN_PATH"] = string.IsNullOrEmpty(existingPluginPath) ? pluginDirectory : pluginDirectory + ":" + existingPluginPath,
                ["GDK_BACKEND"] = "x11"
            };

        /// <summary>Seules les pages web s'ouvrent dans Basilisk.</summary>
        public static bool IsOpenable(string? url, out Uri uri)
        {
            uri = null!;
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) &&
                   (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
                   (uri = parsed) != null;
        }
    }
}
