using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Module Flash Player de l'utilisateur pour Pomme Legacy : reconnaissance du fichier (module
    /// NPAPI 64 bits de ce système) et recherche dans des dossiers, par exemple celui d'un Basilisk
    /// ou d'un Pale Moon portable. Les dossiers fouillés sont choisis par LegacyEngine.
    /// </summary>
    public static partial class FlashModuleSearch
    {
        /// <summary>Module trouvé ; version lue dans le nom du fichier (NPSWF64_32_0_0_371.dll) ou ses propriétés.</summary>
        public sealed record Module(string Path, Version? Version)
        {
            /// <summary>Module 32 bits de Windows : moteur intégré seulement (Basilisk est en 64 bits).</summary>
            public bool Is32Bit => Is32BitModuleName(Path);
        }

        /// <summary>Dossier à fouiller, avec au plus <see cref="Depth"/> niveaux de sous-dossiers.</summary>
        public readonly record struct Location(string Path, int Depth);

        /// <summary>Dernière version de Flash Player sans le blocage des contenus du 12 janvier 2021.</summary>
        public static readonly Version LastWithoutTimeBomb = new(32, 0, 0, 371);

        const int MaxResults = 20;

        // Dossiers sans navigateur portable, souvent immenses : jamais parcourus.
        static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "AppData", "$Recycle.Bin", "System Volume Information", "Windows", "WinSxS",
            "Recovery", "ProgramData", "Cache", "Caches"
        };

        static readonly EnumerationOptions Directories = new()
        {
            IgnoreInaccessible = true,
            // Liens (boucles possibles) et dossiers cachés ou système ignorés.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
        };

        static EnumerationOptions Files(bool windows) => new()
        {
            IgnoreInaccessible = true,
            MatchCasing = windows ? MatchCasing.CaseInsensitive : MatchCasing.CaseSensitive,
            // Un lien vers le module (/usr/lib/mozilla/plugins) compte ; un fichier OneDrive resté en
            // ligne serait téléchargé pour être lu.
            AttributesToSkip = FileAttributes.Offline
        };

        /// <summary>Nom du module NPAPI 64 bits (celui de Basilisk) : NPSWF64_….dll (Windows), libflashplayer.so (Linux).</summary>
        public static bool IsModuleName(string path, bool windows)
        {
            string name = Path.GetFileName(path);
            return windows
                ? name.StartsWith("NPSWF64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                : name == "libflashplayer.so";
        }

        /// <summary>Nom du module NPAPI 32 bits de Windows (NPSWF32_….dll), que seul le moteur intégré utilise.</summary>
        public static bool Is32BitModuleName(string path)
        {
            string name = Path.GetFileName(path);
            return name.StartsWith("NPSWF32", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Module 64 bits, ou 32 bits sous Windows.</summary>
        public static bool IsAnyModuleName(string path, bool windows)
            => IsModuleName(path, windows) || (windows && Is32BitModuleName(path));

        /// <summary>
        /// Bibliothèque de l'architecture qu'annonce son nom : PE « i386 » pour NPSWF32_….dll,
        /// PE « AMD64 » pour les autres (Windows), ELF64 x86-64 (Linux).
        /// </summary>
        public static bool IsModuleBinary(string path, bool windows)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> header = stackalloc byte[64];
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                    return false;

                if (!windows)
                {
                    return header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' &&
                           header[4] == 2 /* 64 bits */ && header[5] == 1 /* petit-boutiste */ &&
                           BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) == 0x3E /* x86-64 */;
                }

                if (header[0] != (byte)'M' || header[1] != (byte)'Z')
                    return false;
                int offset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
                if (offset < 64 || offset > 64 * 1024 || offset > stream.Length - 6)
                    return false;
                stream.Position = offset;
                Span<byte> pe = stackalloc byte[6];
                if (stream.ReadAtLeast(pe, pe.Length, throwOnEndOfStream: false) < pe.Length)
                    return false;
                ushort machine = Is32BitModuleName(path) ? (ushort)0x14C /* i386 */ : (ushort)0x8664 /* AMD64 */;
                return pe[0] == (byte)'P' && pe[1] == (byte)'E' && pe[2] == 0 && pe[3] == 0 &&
                       BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) == machine;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Version du module : nom du fichier de Flash Player (NPSWF64_32_0_0_371.dll, NPSWF32_…), sinon ses propriétés (Windows).</summary>
        public static Version? VersionOf(string path)
        {
            Match match = VersionInName().Match(Path.GetFileName(path));
            if (match.Success &&
                int.TryParse(match.Groups[1].ValueSpan, out int major) && int.TryParse(match.Groups[2].ValueSpan, out int minor) &&
                int.TryParse(match.Groups[3].ValueSpan, out int build) && int.TryParse(match.Groups[4].ValueSpan, out int revision))
            {
                return new Version(major, minor, build, revision);
            }

            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return info.FileMajorPart > 0
                    ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Version qui peut refuser les contenus depuis le 12 janvier 2021 (sauf version modifiée).</summary>
        public static bool MayBlockContent(Module module) => module.Version is { } version && version > LastWithoutTimeBomb;

        /// <summary>
        /// Modules Flash de ce système (64 bits, et 32 bits sous Windows) trouvés dans <paramref name="locations"/> (dans l'ordre,
        /// les moins profonds d'abord), sans ceux de <paramref name="exclude"/> (modules déjà installés)
        /// ni les copies d'un même fichier. S'arrête à l'annulation ou après
        /// <paramref name="maxDirectories"/> dossiers, avec ce qui a été trouvé. Les versions sans le
        /// blocage de 2021 passent en premier.
        /// </summary>
        public static IReadOnlyList<Module> Find(IEnumerable<Location> locations, bool windows, string? exclude, CancellationToken cancellation, int maxDirectories = 60_000)
        {
            StringComparer comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            string? excluded = exclude is { Length: > 0 } ? Normalize(exclude) : null;
            string pattern = windows ? "NPSWF*.dll" : "libflashplayer.so";
            EnumerationOptions files = Files(windows);

            // Profondeur restante la plus grande avec laquelle chaque dossier a été parcouru.
            var visited = new Dictionary<string, int>(comparer);
            var seen = new HashSet<(string, long)>();
            var found = new List<Module>();
            int directories = 0;

            foreach (Location location in locations)
            {
                if (string.IsNullOrWhiteSpace(location.Path))
                    continue;
                var pending = new Queue<(string Path, int Depth)>();
                pending.Enqueue((Normalize(location.Path), Math.Max(0, location.Depth)));

                while (pending.Count > 0 && found.Count < MaxResults && directories < maxDirectories && !cancellation.IsCancellationRequested)
                {
                    (string directory, int depth) = pending.Dequeue();
                    if (visited.TryGetValue(directory, out int done) && done >= depth)
                        continue;
                    bool first = !visited.ContainsKey(directory);
                    visited[directory] = depth;
                    directories++;

                    try
                    {
                        if (!Directory.Exists(directory))
                            continue;
                        if (first && !comparer.Equals(directory, excluded))
                        {
                            foreach (string file in Directory.EnumerateFiles(directory, pattern, files))
                            {
                                if (!IsAnyModuleName(file, windows) || !IsModuleBinary(file, windows))
                                    continue;
                                long length = new FileInfo(file).Length;
                                if (seen.Add((Path.GetFileName(file).ToLowerInvariant(), length)))
                                    found.Add(new Module(file, VersionOf(file)));
                            }
                        }
                        if (depth == 0)
                            continue;
                        foreach (string child in Directory.EnumerateDirectories(directory, "*", Directories))
                        {
                            string name = Path.GetFileName(child);
                            if (name.StartsWith('.') || Skipped.Contains(name))
                                continue;
                            pending.Enqueue((child, depth - 1));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        // Dossier illisible ou retiré entre-temps.
                    }
                }
            }

            // Tri stable : l'ordre de découverte est gardé à l'intérieur de chaque groupe.
            return found.OrderBy(m => MayBlockContent(m) ? 1 : 0).ToList();
        }

        static string Normalize(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        [GeneratedRegex(@"^NPSWF(?:64|32)_(\d+)_(\d+)_(\d+)_(\d+)\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex VersionInName();
    }
}
