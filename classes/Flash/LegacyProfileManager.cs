using System;
using System.IO;

namespace MyHomelabBrowser.classes.Flash
{
    public static class LegacyProfileManager
    {
        public static string GetProfileForDomain(string domain)
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyHomelabBrowser",
                "LegacyProfiles",
                domain
            );

            Directory.CreateDirectory(baseDir);
            return baseDir;
        }
    }
}
