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

        /// <summary>Dossier de configuration du profil par défaut (nom réservé, jamais celui d'un profil créé).</summary>
        public const string DefaultProfileName = "default";

        /// <summary>Profil ouvert, ou null pour le profil par défaut.</summary>
        public static string? ActiveProfile { get; private set; }

        public static string ConfigDirectory => XdgBase("XDG_CONFIG_HOME", ".config");
        public static string BaseDataDirectory => XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share"));
        public static string BaseCacheDirectory => XdgDirectory("XDG_CACHE_HOME", ".cache");

        /// <summary>Données du profil ouvert : historique, cookies, stockage des sites, session.</summary>
        public static string DataDirectory => ProfileDirectory(BaseDataDirectory, ActiveProfile);

        /// <summary>Cache du profil ouvert : cache web, règles anti-pub compilées.</summary>
        public static string CacheDirectory => ProfileDirectory(BaseCacheDirectory, ActiveProfile);

        /// <summary>Fichier du profil (réglages, favoris, services…).</summary>
        public static string Profile(string file) => AppDataContext.GetPath(file);

        public static string Data(string name) => Path.Combine(DataDirectory, name);
        public static string Cache(string name) => Path.Combine(CacheDirectory, name);

        /// <summary>Données communes à tous les profils (modules Flash de Basilisk…).</summary>
        public static string SharedData(string name) => Path.Combine(BaseDataDirectory, name);

        /// <summary>
        /// Le profil par défaut garde les dossiers de base (compatibilité avec les versions
        /// précédentes) ; un profil créé a les siens dans profiles/&lt;nom en minuscules&gt;.
        /// </summary>
        public static string ProfileDirectory(string root, string? profile)
            => string.IsNullOrWhiteSpace(profile)
                ? root
                : Path.Combine(root, "profiles", profile.Trim().ToLowerInvariant());

        /// <summary>À appeler avant ProfileService, qui lit le dossier de configuration.</summary>
        public static void Initialize()
        {
            // .NET ne renvoie le dossier de configuration que s'il existe déjà (compte neuf :
            // chemin vide, et le profil serait créé dans le dossier courant).
            Directory.CreateDirectory(ConfigDirectory);
        }

        /// <summary>Ouvre un profil (null : profil par défaut) : réglages, données et cache.</summary>
        public static void UseProfile(string? name)
        {
            ActiveProfile = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            AppDataContext.UseProfile(ActiveProfile ?? DefaultProfileName);
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
        public static string DefaultDownloadDirectory() => UserDirectory("XDG_DOWNLOAD_DIR", "Downloads");

        /// <summary>Dossier « Documents » de l'utilisateur (rapports enregistrés), sinon ~/Documents.</summary>
        public static string DocumentsDirectory() => UserDirectory("XDG_DOCUMENTS_DIR", "Documents");

        static string UserDirectory(string key, string fallback)
        {
            try
            {
                string file = Path.Combine(ConfigDirectory, "user-dirs.dirs");
                if (File.Exists(file) && ParseUserDirs(File.ReadAllLines(file), Home).TryGetValue(key, out string? dir))
                    return dir;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return Path.Combine(Home, fallback);
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
