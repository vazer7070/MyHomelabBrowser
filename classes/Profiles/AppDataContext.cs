using System;
using System.IO;

namespace MyHomelabBrowser.classes.Profiles
{
    static class AppDataContext
    {
        static string _currentRoot = DefaultRoot;

        static string DefaultRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MyHomelabBrowser"
            );

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
