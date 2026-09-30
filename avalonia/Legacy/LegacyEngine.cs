using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;
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

        /// <summary>Version du moteur livré (2026.09.24), lue dans son application.ini ; null s'il est absent.</summary>
        public static string? BundledVersion
        {
            get
            {
                if (BundledExecutable is not { } executable)
                    return null;
                try
                {
                    string ini = Path.Combine(Path.GetDirectoryName(executable)!, "application.ini");
                    if (!File.Exists(ini))
                        return null;
                    string? version = PommeBrowser.Linux.Core.BasiliskInstall.ParseApplicationIni(File.ReadAllLines(ini)).Version;
                    // Basilisk se présente aux modules comme « 52.9.<date> ».
                    return version is { } v && v.StartsWith("52.9.", StringComparison.Ordinal) ? v[5..] : version;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

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
        public static bool IsModuleName(string path) => FlashModuleSearch.IsModuleName(path, OperatingSystem.IsWindows());

        /// <summary>
        /// Modules Flash de l'ordinateur (hors module déjà installé) : dans <paramref name="folder"/>
        /// et ses sous-dossiers, ou, sans dossier, là où Flash Player s'installait et là où l'on range
        /// d'habitude un navigateur portable (Basilisk, Pale Moon…). Vingt secondes au plus.
        /// </summary>
        public static IReadOnlyList<FlashModuleSearch.Module> FindModules(string? folder = null)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            IEnumerable<FlashModuleSearch.Location> locations = folder != null
                ? new[] { new FlashModuleSearch.Location(folder, 10) }
                : SearchLocations();
            return FlashModuleSearch.Find(locations, OperatingSystem.IsWindows(), PluginDirectory, budget.Token);
        }

        /// <summary>Dossiers fouillés par la recherche automatique, des plus probables aux plus larges.</summary>
        static IEnumerable<FlashModuleSearch.Location> SearchLocations()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            char separator = OperatingSystem.IsWindows() ? ';' : ':';
            IEnumerable<string> pluginPath = (Environment.GetEnvironmentVariable("MOZ_PLUGIN_PATH") ?? string.Empty)
                .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (OperatingSystem.IsWindows())
            {
                // Flash Player installé pour Firefox (version 64 bits dans System32).
                yield return new(Path.Combine(Environment.SystemDirectory, "Macromed", "Flash"), 0);
                foreach (string directory in pluginPath)
                    yield return new(directory, 0);
                foreach (string directory in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Path.Combine(home, "Downloads"),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Path.Combine(home, "PortableApps")
                })
                {
                    yield return new(directory, 6);
                }
                yield return new(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 3);
                yield return new(home, 3);
                // Autres disques : D:\Jeux\BasiliskPortable\…
                foreach (string drive in FixedDrives())
                    yield return new(drive, 4);
                yield break;
            }

            foreach (string directory in new[]
            {
                "/usr/lib/flashplugin-nonfree", "/usr/lib/adobe-flashplugin", "/usr/lib/flashplugin-installer",
                "/usr/lib/mozilla/plugins", "/usr/lib64/mozilla/plugins", "/usr/lib/x86_64-linux-gnu/mozilla/plugins",
                "/usr/local/lib/mozilla/plugins", "/usr/lib/browser-plugins", "/usr/lib64/browser-plugins",
                Path.Combine(home, ".mozilla", "plugins")
            })
            {
                yield return new(directory, 0);
            }
            foreach (string directory in pluginPath)
                yield return new(directory, 0);
            foreach (string directory in new[] { LinuxPaths.DesktopDirectory(), LinuxPaths.DefaultDownloadDirectory(), LinuxPaths.DocumentsDirectory() })
                yield return new(directory, 6);
            yield return new("/opt", 3);
            yield return new(home, 3);
        }

        static List<string> FixedDrives()
        {
            try
            {
                return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new List<string>();
            }
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
            if (!FlashModuleSearch.IsModuleBinary(source, OperatingSystem.IsWindows()))
                return Tr("Ce fichier n'est pas un module Flash 64 bits valide.");

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
    }
}
