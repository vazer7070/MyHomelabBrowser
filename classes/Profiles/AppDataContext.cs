using System;
using System.IO;

namespace MyHomelabBrowser.classes.Profiles
{
    static class AppDataContext
    {
        static string? _baseOverride;
        static string _currentRoot = DefaultRoot;

        static string DefaultRoot =>
            _baseOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MyHomelabBrowser"
            );

        /// <summary>
        /// Racine propre au système (macOS : ~/Library/Application Support/PommeBrowser),
        /// à définir avant toute lecture de profil.
        /// </summary>
        public static void UseBaseDirectory(string path)
        {
            _baseOverride = path;
            _currentRoot = path;
            Directory.CreateDirectory(path);
        }

        public static string Root => _currentRoot;

        public static string GlobalRoot => DefaultRoot;

        public static void UseProfile(string profileName)
        {
            _currentRoot = Path.Combine(
                DefaultRoot,
                "profiles",
                profileName.ToLowerInvariant()
            );

            Directory.CreateDirectory(_currentRoot);
        }

        public static void UseGlobal()
        {
            _currentRoot = DefaultRoot;
        }

        public static string GetPath(string file)
            => Path.Combine(_currentRoot, file);
    }
}
