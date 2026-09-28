using MyHomelabBrowser.classes.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Profils Basilisk : un par site (cookies et connexions conservés), un profil
    /// jetable par onglet privé. Un profil ne peut servir qu'à un seul Basilisk à la
    /// fois : le deuxième onglet Legacy d'un même site reçoit l'emplacement suivant
    /// (« site~2 », « site~3 »…), au lieu d'échouer sur « Basilisk est déjà ouvert ».
    /// </summary>
    public static class LegacyProfileManager
    {
        public const int MaxSlotsPerSite = 8;

        static readonly object Sync = new();
        static readonly HashSet<string> Leased = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Dossier racine des profils ; modifiable par les tests et par l'édition Linux.</summary>
        internal static string? RootOverride { get; set; }

        static string Root => RootOverride ?? Path.Combine(AppDataContext.Root, "flash", "legacy");

        public static LegacyProfileLease CreateLease(string domain, bool isPrivate)
        {
            CleanupStalePrivateProfiles();
            string safeDomain = Sanitize(domain);

            if (isPrivate)
            {
                string privatePath = Path.Combine(Root, "private", safeDomain + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(privatePath);
                return new LegacyProfileLease(privatePath, isTemporary: true, release: null);
            }

            lock (Sync)
            {
                for (int slot = 1; slot <= MaxSlotsPerSite; slot++)
                {
                    string path = Path.Combine(Root, "profiles", slot == 1 ? safeDomain : $"{safeDomain}~{slot}");
                    if (Leased.Contains(path) || IsLockedByAnotherProcess(path))
                        continue;

                    Directory.CreateDirectory(path);
                    Leased.Add(path);
                    return new LegacyProfileLease(path, isTemporary: false, release: () =>
                    {
                        lock (Sync)
                            Leased.Remove(path);
                    });
                }
            }

            throw new InvalidOperationException(
                Localization.Loc.Tr("Trop d’onglets Legacy sont ouverts sur ce site (maximum {0}).", MaxSlotsPerSite));
        }

        /// <summary>
        /// Basilisk garde parent.lock ouvert sans partage tant qu'il utilise le profil
        /// (le fichier disparaît à sa fermeture). Un Basilisk lancé à la main sur ce
        /// profil, ou resté d'une ancienne version, le rend donc indisponible.
        /// </summary>
        internal static bool IsLockedByAnotherProcess(string profilePath)
        {
            if (OperatingSystem.IsLinux())
                return IsLockedOnLinux(profilePath);

            string lockFile = Path.Combine(profilePath, "parent.lock");
            if (!File.Exists(lockFile))
                return false;

            try
            {
                using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>
        /// Sous Linux, Basilisk signale un profil ouvert par le lien symbolique « lock » vers
        /// « adresse:+PID ». Un lien laissé par un Basilisk arrêté brutalement ne bloque pas le profil.
        /// </summary>
        internal static bool IsLockedOnLinux(string profilePath)
        {
            try
            {
                string? target = new FileInfo(Path.Combine(profilePath, "lock")).LinkTarget;
                int plus = target?.LastIndexOf('+') ?? -1;
                return plus >= 0 &&
                       int.TryParse(target![(plus + 1)..], out int pid) &&
                       pid > 0 &&
                       Directory.Exists("/proc/" + pid);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void CleanupStalePrivateProfiles()
        {
            string privateRoot = Path.Combine(Root, "private");
            if (!Directory.Exists(privateRoot))
                return;

            foreach (string directory in Directory.EnumerateDirectories(privateRoot))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromHours(12) &&
                        !IsLockedByAnotherProcess(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        internal static string Sanitize(string domain)
        {
            string normalized = (domain ?? "site")
                .Trim()
                .TrimEnd('.')
                .ToLowerInvariant();

            if (normalized.Length == 0)
                normalized = "site";

            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(normalized.Select(c => invalid.Contains(c) || c == '~' ? '_' : c).ToArray());
        }
    }
}
