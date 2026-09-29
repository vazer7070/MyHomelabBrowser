using System;
using System.IO;
using System.Linq;
using PommeBrowser.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Moteur Flash d'origine livré avec PommeBrowser (Basilisk compilé depuis ses sources, dans le
    /// dossier legacy/ de l'application) et module Flash Player de l'utilisateur. Adobe interdit de
    /// redistribuer Flash Player : PommeBrowser ne le fournit pas, l'utilisateur choisit sa copie,
    /// qui est rangée dans les données communes aux profils.
    /// </summary>
    public static class LegacyEngine
    {
        static readonly Lazy<string?> Bundled = new(FindBundled);

        /// <summary>Moteur livré avec l'application ; null s'il est absent (compilation sans lui, macOS).</summary>
        public static string? BundledExecutable => Bundled.Value;

        /// <summary>Dossier des modules (MOZ_PLUGIN_PATH), commun à tous les profils.</summary>
        public static string PluginDirectory => AppPaths.SharedData("plugins");

        /// <summary>Nom attendu du module Flash sur ce système.</summary>
        public static string ExpectedModuleName => OperatingSystem.IsWindows() ? "NPSWF64_….dll" : "libflashplayer.so";

        static string? FindBundled()
        {
            if (OperatingSystem.IsMacOS())
                return null;
            string path = Path.Combine(AppContext.BaseDirectory, "legacy", OperatingSystem.IsWindows() ? "basilisk.exe" : "basilisk");
            return File.Exists(path) ? path : null;
        }

        /// <summary>Module Flash installé pour Basilisk (chemin), ou null.</summary>
        public static string? InstalledModule
        {
            get
            {
                try
                {
                    return Directory.Exists(PluginDirectory)
                        ? Directory.EnumerateFiles(PluginDirectory).Where(IsModuleName).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).LastOrDefault()
                        : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        /// <summary>Module Flash NPAPI 64 bits de ce système (le moteur est en 64 bits).</summary>
        public static bool IsModuleName(string path)
        {
            string name = Path.GetFileName(path);
            return OperatingSystem.IsWindows()
                ? name.StartsWith("NPSWF64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                : name == "libflashplayer.so";
        }

        /// <summary>
        /// Copie le module Flash choisi par l'utilisateur. Retourne un message d'erreur, ou null
        /// si le module est installé. Un module précédent est remplacé.
        /// </summary>
        public static string? InstallModule(string source)
        {
            if (!File.Exists(source))
                return Tr("Fichier introuvable.");

            string name = Path.GetFileName(source);
            if (OperatingSystem.IsWindows() && name.StartsWith("NPSWF32", StringComparison.OrdinalIgnoreCase))
                return Tr("Ce module Flash est en 32 bits. Choisissez la version 64 bits ({0}).", ExpectedModuleName);
            if (!IsModuleName(source))
                return Tr("Choisissez le module Flash Player pour navigateurs NPAPI : {0}.", ExpectedModuleName);
            if (!HasBinaryHeader(source))
                return Tr("Ce fichier n'est pas un module Flash valide.");

            try
            {
                Directory.CreateDirectory(PluginDirectory);
                foreach (string previous in Directory.EnumerateFiles(PluginDirectory).Where(IsModuleName).ToList())
                {
                    if (!string.Equals(Path.GetFileName(previous), name, StringComparison.OrdinalIgnoreCase))
                        File.Delete(previous);
                }
                File.Copy(source, Path.Combine(PluginDirectory, name), overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Windows : Basilisk ne lit pas MOZ_PLUGIN_PATH, il cherche ses modules dans son propre
        /// dossier plugins\. Le module de l'utilisateur y est recopié avant le lancement (le dossier
        /// de l'application est remplacé à chaque mise à jour). Sans effet ailleurs.
        /// </summary>
        public static void PrepareAppPlugins(string executable)
        {
            if (!OperatingSystem.IsWindows() || InstalledModule is not { } module)
                return;
            try
            {
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "plugins");
                string target = Path.Combine(directory, Path.GetFileName(module));
                var source = new FileInfo(module);
                var existing = new FileInfo(target);
                if (existing.Exists && existing.Length == source.Length && existing.LastWriteTimeUtc == source.LastWriteTimeUtc)
                    return;
                Directory.CreateDirectory(directory);
                foreach (string previous in Directory.EnumerateFiles(directory).Where(IsModuleName).ToList())
                    File.Delete(previous);
                File.Copy(module, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MyHomelabBrowser.classes.RuntimeLogBuffer.Append("[Basilisk] Module Flash non copié : " + ex.Message);
            }
        }

        /// <summary>Bibliothèque du système : en-tête ELF (Linux) ou MZ (Windows).</summary>
        static bool HasBinaryHeader(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[4];
                if (stream.Read(header) < 4)
                    return false;
                return OperatingSystem.IsWindows()
                    ? header[0] == (byte)'M' && header[1] == (byte)'Z'
                    : header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F';
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
