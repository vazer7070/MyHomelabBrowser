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
    /// Module Flash Player de l'utilisateur : reconnaissance du fichier (module NPAPI, 64 bits, ou
    /// 32 bits sous Windows pour le moteur intégré) et recherche dans des dossiers, par exemple
    /// celui d'un Basilisk ou d'un Pale Moon portable. L'architecture est lue dans le fichier, pas
    /// dans son nom. Les dossiers fouillés sont choisis par LegacyEngine.
    /// </summary>
    public static partial class FlashModuleSearch
    {
        /// <summary>Architecture d'un module ; <see cref="ModuleArchitecture.Unknown"/> : pas une bibliothèque utilisable ici.</summary>
        public enum ModuleArchitecture
        {
            Unknown,
            X86,
            X64
        }

        /// <summary>
        /// Module trouvé ; version lue dans le nom du fichier (NPSWF64_32_0_0_371.dll) ou ses
        /// propriétés. 32 bits : Windows seulement, pour le moteur intégré (Basilisk est en 64 bits).
        /// </summary>
        public sealed record Module(string Path, Version? Version, bool Is32Bit = false);

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

        /// <summary>
        /// Nom du module Flash NPAPI : NPSWF….dll sous Windows (NPSWF64_…, NPSWF32_…, NPSWF32.dll
        /// des anciennes versions), libflashplayer.so sous Linux.
        /// </summary>
        public static bool IsModuleName(string path, bool windows)
        {
            string name = Path.GetFileName(path);
            return windows
                ? name.StartsWith("NPSWF", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                : name == "libflashplayer.so";
        }

        /// <summary>
        /// Architecture lue dans l'en-tête de la bibliothèque : PE « AMD64 » ou « i386 » (Windows),
        /// ELF64 x86-64 (Linux, où seul le 64 bits est pris en charge).
        /// </summary>
        public static ModuleArchitecture ArchitectureOf(string path, bool windows)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> header = stackalloc byte[64];
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                    return ModuleArchitecture.Unknown;

                if (!windows)
                {
                    return header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' &&
                           header[4] == 2 /* 64 bits */ && header[5] == 1 /* petit-boutiste */ &&
                           BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) == 0x3E /* x86-64 */
                        ? ModuleArchitecture.X64
                        : ModuleArchitecture.Unknown;
                }

                if (header[0] != (byte)'M' || header[1] != (byte)'Z')
                    return ModuleArchitecture.Unknown;
                int offset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
                if (offset < 64 || offset > 64 * 1024 || offset > stream.Length - 6)
                    return ModuleArchitecture.Unknown;
                stream.Position = offset;
                Span<byte> pe = stackalloc byte[6];
                if (stream.ReadAtLeast(pe, pe.Length, throwOnEndOfStream: false) < pe.Length ||
                    pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0)
                {
                    return ModuleArchitecture.Unknown;
                }
                return BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) switch
                {
                    0x8664 => ModuleArchitecture.X64,
                    0x14C => ModuleArchitecture.X86,
                    _ => ModuleArchitecture.Unknown
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ModuleArchitecture.Unknown;
            }
        }

        /// <summary>Bibliothèque utilisable sur ce système : 64 bits, ou 32 bits sous Windows.</summary>
        public static bool IsModuleBinary(string path, bool windows) => ArchitectureOf(path, windows) != ModuleArchitecture.Unknown;

        /// <summary>Module 32 bits (Windows) : il ne peut servir qu'au moteur intégré, avec son hôte 32 bits.</summary>
        public static bool Is32Bit(string path) => ArchitectureOf(path, windows: true) == ModuleArchitecture.X86;

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
        /// Le module à garder pour chaque architecture (64 bits d'abord) : une version sans le
        /// blocage de 2021 avant les autres, puis la plus récente, et une version inconnue en
        /// dernier ; à égalité, le premier de la liste (le module déjà installé).
        /// </summary>
        public static IReadOnlyList<Module> Best(IEnumerable<Module> modules)
            => modules.GroupBy(m => m.Is32Bit)
                .Select(group => group
                    .OrderBy(m => m.Version == null ? 2 : MayBlockContent(m) ? 1 : 0)
                    .ThenByDescending(m => m.Version ?? new Version(0, 0))
                    .First())
                .OrderBy(m => m.Is32Bit ? 1 : 0)
                .ToList();

        /// <summary>
        /// Ordre d'essai du moteur intégré : une version sans le blocage de 2021 d'abord, puis le
        /// module 32 bits (celui des navigateurs de l'époque de Flash).
        /// </summary>
        public static IEnumerable<Module> IntegratedEngineOrder(IEnumerable<Module> modules)
            => modules.OrderBy(m => MayBlockContent(m) ? 1 : 0).ThenBy(m => m.Is32Bit ? 0 : 1);

        /// <summary>
        /// Modules Flash de ce système (64 bits, et 32 bits sous Windows) trouvés dans <paramref name="locations"/> (dans l'ordre,
        /// les moins profonds d'abord), sans ceux de <paramref name="exclude"/> et de ses sous-dossiers
        /// (modules déjà installés) ni les copies d'un même fichier. S'arrête à l'annulation ou après
        /// <paramref name="maxDirectories"/> dossiers, avec ce qui a été trouvé. Les versions sans le
        /// blocage de 2021 passent en premier.
        /// </summary>
        public static IReadOnlyList<Module> Find(IEnumerable<Location> locations, bool windows, string? exclude, CancellationToken cancellation, int maxDirectories = 60_000)
        {
            StringComparer comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            StringComparison comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
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
                        bool installed = excluded != null &&
                                         (comparer.Equals(directory, excluded) || directory.StartsWith(excluded + Path.DirectorySeparatorChar, comparison));
                        if (first && !installed)
                        {
                            foreach (string file in Directory.EnumerateFiles(directory, pattern, files))
                            {
                                if (!IsModuleName(file, windows))
                                    continue;
                                ModuleArchitecture architecture = ArchitectureOf(file, windows);
                                if (architecture == ModuleArchitecture.Unknown)
                                    continue;
                                long length = new FileInfo(file).Length;
                                if (seen.Add((Path.GetFileName(file).ToLowerInvariant(), length)))
                                    found.Add(new Module(file, VersionOf(file), architecture == ModuleArchitecture.X86));
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
