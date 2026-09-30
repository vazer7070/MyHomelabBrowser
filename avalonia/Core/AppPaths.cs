using System;
using System.IO;
using MyHomelabBrowser.classes.Profiles;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Emplacements des données selon le système, identiques à ceux des éditions précédentes
    /// pour qu'un profil existant soit repris tel quel :
    /// Windows comme l'édition WPF (%APPDATA%\MyHomelabBrowser, WebView2 dans %LOCALAPPDATA%\PommeBrowser),
    /// Linux comme l'édition GTK (XDG : ~/.config, ~/.local/share/pommebrowser, ~/.cache/pommebrowser),
    /// macOS dans ~/Library (Application Support et Caches).
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Profil ouvert, ou null pour le profil par défaut.</summary>
        public static string? ActiveProfile { get; private set; }

        static string ProfileId => ActiveProfile?.Trim().ToLowerInvariant() ?? LinuxPaths.DefaultProfileName;

        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        static string MacSupport => Path.Combine(Home, "Library", "Application Support", "PommeBrowser");

        /// <summary>À appeler en premier, avant la lecture des profils.</summary>
        public static void Initialize()
        {
            if (OperatingSystem.IsLinux())
                LinuxPaths.Initialize();
            else if (OperatingSystem.IsMacOS())
                AppDataContext.UseBaseDirectory(MacSupport);
        }

        /// <summary>Ouvre un profil (null : profil par défaut) : réglages, données et cache.</summary>
        public static void UseProfile(string? name)
        {
            ActiveProfile = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

            if (OperatingSystem.IsLinux())
            {
                LinuxPaths.UseProfile(ActiveProfile);
                return;
            }

            // Comme l'édition Windows : réglages du profil par défaut à la racine.
            if (ActiveProfile == null)
                AppDataContext.UseGlobal();
            else
                AppDataContext.UseProfile(ActiveProfile);

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(CacheDirectory);
        }

        /// <summary>Historique, session, cookies (Linux) du profil ouvert.</summary>
        public static string DataDirectory =>
            OperatingSystem.IsLinux() ? LinuxPaths.DataDirectory :
            OperatingSystem.IsMacOS() ? Path.Combine(MacSupport, "profiles", ProfileId) :
            Path.Combine(AppDataContext.GlobalRoot, "profiles", ActiveProfile?.Trim() ?? LinuxPaths.DefaultProfileName);

        /// <summary>Fichiers régénérables (règles anti-pub compilées…).</summary>
        public static string CacheDirectory =>
            OperatingSystem.IsLinux() ? LinuxPaths.CacheDirectory :
            OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Caches", "PommeBrowser", ProfileId) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PommeBrowser", "Cache", ProfileId);

        public static string Data(string name) => Path.Combine(DataDirectory, name);
        public static string Cache(string name) => Path.Combine(CacheDirectory, name);

        /// <summary>Fichier de réglages du profil (settings.json, services, coffre…).</summary>
        public static string Profile(string name) => AppDataContext.GetPath(name);

        /// <summary>Données communes à tous les profils (modules de Basilisk…).</summary>
        public static string SharedData(string name) =>
            OperatingSystem.IsLinux() ? LinuxPaths.SharedData(name) :
            OperatingSystem.IsMacOS() ? Path.Combine(MacSupport, name) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PommeBrowser", name);

        /// <summary>Favoris : dans les réglages du profil sous Linux, dans ses données ailleurs (comme avant).</summary>
        public static string FavoritesFile => OperatingSystem.IsLinux() ? Profile("favorites.json") : Data("favorites.json");

        public static string HistoryDatabase => Data("history.db");

        /// <summary>Onglets de la dernière session.</summary>
        public static string SessionFile =>
            OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyHomelabBrowser", ActiveProfile == null ? "session.json" : "session-" + ProfileId + ".json")
                : Data("session.json");

        /// <summary>Cookies de WebKitGTK (base SQLite du profil).</summary>
        public static string? CookieDatabase => OperatingSystem.IsLinux() ? Data("cookies.sqlite") : null;

        /// <summary>
        /// Nom du programme donné à GLib avant le démarrage de WebKitGTK : ses données et son
        /// cache suivent ce nom (~/.local/share/&lt;nom&gt;), soit le dossier « webkit » du profil.
        /// </summary>
        public static string GlibProgramName =>
            ActiveProfile == null ? "pommebrowser/webkit" : "pommebrowser/profiles/" + ProfileId + "/webkit";

        /// <summary>Données WebView2 du profil (même dossier que l'édition Windows).</summary>
        public static string WebView2UserDataFolder => WebViewProfileData.GetUserDataFolder(ActiveProfile);

        /// <summary>
        /// Magasin de données WebKit du profil (macOS 14 et suivants) : identifiant stable tiré du
        /// nom du profil, pour que chaque profil garde ses propres cookies et sessions.
        /// </summary>
        public static Guid AppleDataStoreId
            => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("pommebrowser:" + ProfileId)));

        /// <summary>Dossier Téléchargements de l'utilisateur.</summary>
        public static string DefaultDownloadDirectory =>
            OperatingSystem.IsLinux() ? LinuxPaths.DefaultDownloadDirectory() : Path.Combine(Home, "Downloads");

        /// <summary>Dossier Documents (rapports enregistrés).</summary>
        public static string DocumentsDirectory =>
            OperatingSystem.IsLinux() ? LinuxPaths.DocumentsDirectory() : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
