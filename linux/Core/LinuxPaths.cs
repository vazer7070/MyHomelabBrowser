using System;
using System.Collections.Generic;
using System.IO;
using MyHomelabBrowser.classes.Profiles;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Emplacements des données selon les conventions XDG :
    /// réglages, favoris et services dans ~/.config/MyHomelabBrowser (même organisation
    /// que l'édition Windows), historique et données des sites dans ~/.local/share/pommebrowser,
    /// fichiers régénérables (cache, règles anti-pub compilées) dans ~/.cache/pommebrowser.
    /// </summary>
    public static class LinuxPaths
    {
        public const string AppId = "io.github.vazer7070.PommeBrowser";
        public const string ProfileName = "default";

        public static string ConfigDirectory => XdgBase("XDG_CONFIG_HOME", ".config");
        public static string DataDirectory => XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share"));
        public static string CacheDirectory => XdgDirectory("XDG_CACHE_HOME", ".cache");

        /// <summary>Fichier du profil (réglages, favoris, services…).</summary>
        public static string Profile(string file) => AppDataContext.GetPath(file);

        public static string Data(string name) => Path.Combine(DataDirectory, name);
        public static string Cache(string name) => Path.Combine(CacheDirectory, name);

        public static void Initialize()
        {
            // .NET ne renvoie le dossier de configuration que s'il existe déjà (compte neuf :
            // chemin vide, et le profil serait créé dans le dossier courant).
            Directory.CreateDirectory(ConfigDirectory);
            AppDataContext.UseProfile(ProfileName);
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(CacheDirectory);
        }

        static string XdgDirectory(string variable, string fallback)
            => Path.Combine(XdgBase(variable, fallback), "pommebrowser");

        static string XdgBase(string variable, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value)
                ? value
                : Path.Combine(Home, fallback);
        }

        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>
        /// Dossier « Téléchargements » de l'utilisateur (XDG_DOWNLOAD_DIR de ~/.config/user-dirs.dirs,
        /// traduit selon la langue du système), sinon ~/Downloads.
        /// </summary>
        public static string DefaultDownloadDirectory()
        {
            try
            {
                string file = Path.Combine(ConfigDirectory, "user-dirs.dirs");
                if (File.Exists(file) && ParseUserDirs(File.ReadAllLines(file), Home).TryGetValue("XDG_DOWNLOAD_DIR", out string? dir))
                    return dir;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return Path.Combine(Home, "Downloads");
        }

        /// <summary>Lecture du format de user-dirs.dirs : XDG_DOWNLOAD_DIR="$HOME/Téléchargements".</summary>
        public static Dictionary<string, string> ParseUserDirs(IEnumerable<string> lines, string home)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (line.StartsWith('#') || equals <= 0)
                    continue;

                string value = line[(equals + 1)..].Trim().Trim('"');
                if (value.StartsWith("$HOME", StringComparison.Ordinal))
                    value = home + value[5..];

                // « $HOME/ » seul désigne le dossier personnel : pas un vrai dossier de téléchargement.
                if (Path.IsPathRooted(value) && Path.TrimEndingDirectorySeparator(value) != Path.TrimEndingDirectorySeparator(home))
                    result[line[..equals].Trim()] = Path.TrimEndingDirectorySeparator(value);
            }
            return result;
        }
    }
}
