using MyHomelabBrowser.classes.Profiles;
using System;
using System.IO;
using System.Linq;

namespace MyHomelabBrowser.classes.Flash
{
    public static class LegacyProfileManager
    {
        public static LegacyProfileLease CreateLease(string domain, bool isPrivate)
        {
            CleanupStalePrivateProfiles();
            string safeDomain = Sanitize(domain);
            string root = Path.Combine(AppDataContext.Root, "flash", "legacy");

            string profilePath = isPrivate
                ? Path.Combine(root, "private", safeDomain + "-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(root, "profiles", safeDomain);

            Directory.CreateDirectory(profilePath);
            return new LegacyProfileLease(profilePath, isTemporary: isPrivate);
        }

        // Compatibilité avec l'ancien code. Les nouveaux lancements doivent utiliser CreateLease.
        public static string GetProfileForDomain(string domain)
        {
            string path = Path.Combine(
                AppDataContext.Root,
                "flash",
                "legacy",
                "profiles",
                Sanitize(domain));

            Directory.CreateDirectory(path);
            return path;
        }


        private static void CleanupStalePrivateProfiles()
        {
            string privateRoot = Path.Combine(AppDataContext.Root, "flash", "legacy", "private");
            if (!Directory.Exists(privateRoot))
                return;

            foreach (string directory in Directory.EnumerateDirectories(privateRoot))
            {
                try
                {
                    DateTime lastWrite = Directory.GetLastWriteTimeUtc(directory);
                    if (DateTime.UtcNow - lastWrite > TimeSpan.FromHours(12))
                        Directory.Delete(directory, recursive: true);
                }
                catch { }
            }
        }

        private static string Sanitize(string domain)
        {
            string normalized = (domain ?? "site")
                .Trim()
                .TrimEnd('.')
                .ToLowerInvariant();

            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(normalized.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
