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
        public static string ExpectedModuleName => OperatingSystem.IsWindows() ? "NPSWF64_….dll, NPSWF32_….dll" : "libflashplayer.so";

        static string? FindBundled()
        {
            if (OperatingSystem.IsMacOS())
                return null;
            string path = Path.Combine(AppContext.BaseDirectory, "legacy", OperatingSystem.IsWindows() ? "basilisk.exe" : "basilisk");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Module 32 bits (Windows), pour le moteur intégré seulement : rangé à part, dans un
        /// sous-dossier que Basilisk (64 bits) ne parcourt pas.
        /// </summary>
        public static string PluginDirectory32 => Path.Combine(PluginDirectory, "x86");

        /// <summary>Module Flash installé pour Basilisk et le moteur intégré (64 bits ; chemin), ou null.</summary>
        public static string? InstalledModule => ModuleIn(PluginDirectory);

        /// <summary>Module Flash 32 bits installé pour le moteur intégré (Windows), ou null.</summary>
        public static string? InstalledModule32 => OperatingSystem.IsWindows() ? ModuleIn(PluginDirectory32) : null;

        static string? ModuleIn(string directory)
        {
            try
            {
                return Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory).Where(IsModuleName).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).LastOrDefault()
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Modules du moteur intégré (Windows, Linux), dans l'ordre où les essayer : un par
        /// architecture, la copie installée dans PommeBrowser, sinon le Flash Player installé dans
        /// le système (Windows : Macromed\Flash ; Linux : dossiers de modules des navigateurs),
        /// utilisé en place. Une version sans le blocage de 2021 passe en premier, puis le module
        /// 32 bits (celui des navigateurs d'époque ; Windows seulement, l'hôte de Linux est 64 bits) ;
        /// le suivant sert de secours si le premier ne se charge pas.
        /// </summary>
        public static IReadOnlyList<string> IntegratedModules
        {
            get
            {
                bool windows = OperatingSystem.IsWindows();
                if (!windows && !OperatingSystem.IsLinux())
                    return Array.Empty<string>();
                var candidates = new List<FlashModuleSearch.Module>();
                foreach (string path in new[] { InstalledModule32, InstalledModule }.OfType<string>().Concat(SystemModules()))
                {
                    FlashModuleSearch.ModuleArchitecture architecture = FlashModuleSearch.ArchitectureOf(path, windows);
                    bool is32Bit = architecture == FlashModuleSearch.ModuleArchitecture.X86;
                    if (architecture == FlashModuleSearch.ModuleArchitecture.Unknown || (!windows && is32Bit) || candidates.Any(c => c.Is32Bit == is32Bit))
                        continue;
                    candidates.Add(new FlashModuleSearch.Module(path, FlashModuleSearch.VersionOf(path), is32Bit));
                }
                return FlashModuleSearch.IntegratedEngineOrder(candidates).Select(m => m.Path).ToList();
            }
        }

        /// <summary>
        /// Flash Player installé dans le système : sous Windows, pour Firefox (64 bits dans
        /// System32, 32 bits dans SysWOW64) ; sous Linux, dans les dossiers de modules des navigateurs.
        /// </summary>
        static IEnumerable<string> SystemModules()
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (string system in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(system))
                        continue;
                    if (ModuleIn(Path.Combine(system, "Macromed", "Flash")) is { } module)
                        yield return module;
                }
                yield break;
            }
            foreach (string directory in LinuxModuleDirectories(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            {
                if (ModuleIn(directory) is { } module)
                    yield return module;
            }
        }

        /// <summary>Dossiers où les navigateurs de Linux cherchent leurs modules (libflashplayer.so).</summary>
        static string[] LinuxModuleDirectories(string home) => new[]
        {
            "/usr/lib/flashplugin-nonfree", "/usr/lib/adobe-flashplugin", "/usr/lib/flashplugin-installer",
            "/usr/lib/mozilla/plugins", "/usr/lib64/mozilla/plugins", "/usr/lib/x86_64-linux-gnu/mozilla/plugins",
            "/usr/local/lib/mozilla/plugins", "/usr/lib/browser-plugins", "/usr/lib64/browser-plugins",
            Path.Combine(home, ".mozilla", "plugins")
        };

        /// <summary>Retire le module 32 bits installé : le moteur intégré reprend l'autre module.</summary>
        public static string? RemoveModule32()
        {
            try
            {
                if (Directory.Exists(PluginDirectory32))
                {
                    foreach (string module in Directory.EnumerateFiles(PluginDirectory32).Where(IsModuleName).ToList())
                        File.Delete(module);
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        /// <summary>Nom d'un module Flash NPAPI de ce système (NPSWF….dll, libflashplayer.so), toute architecture.</summary>
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
                // Flash Player installé pour Firefox (64 bits dans System32, 32 bits dans SysWOW64).
                yield return new(Path.Combine(Environment.SystemDirectory, "Macromed", "Flash"), 0);
                yield return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "Macromed", "Flash"), 0);
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

            foreach (string directory in LinuxModuleDirectories(home))
                yield return new(directory, 0);
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
        /// si le module est installé. L'architecture est lue dans le fichier : le module 64 bits
        /// sert à Basilisk et au moteur intégré, le module 32 bits (Windows) au moteur intégré
        /// seulement. Un module précédent de la même architecture est remplacé.
        /// </summary>
        public static string? InstallModule(string source)
        {
            if (!File.Exists(source))
                return Tr("Fichier introuvable.");

            bool windows = OperatingSystem.IsWindows();
            string name = Path.GetFileName(source);
            if (!IsModuleName(source))
            {
                // Flash de Chrome (PPAPI) ou d'Internet Explorer (ActiveX) : une autre interface.
                return name.Contains("pepflash", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ocx", StringComparison.OrdinalIgnoreCase)
                    ? Tr("Ce Flash est celui de Chrome ou d'Internet Explorer. Il faut le module Flash Player pour navigateurs NPAPI (Firefox) : {0}.", ExpectedModuleName)
                    : Tr("Choisissez le module Flash Player pour navigateurs NPAPI : {0}.", ExpectedModuleName);
            }
            FlashModuleSearch.ModuleArchitecture architecture = FlashModuleSearch.ArchitectureOf(source, windows);
            if (architecture == FlashModuleSearch.ModuleArchitecture.Unknown)
                return Tr("Ce fichier n'est pas un module Flash valide ({0}).", name);

            string directory = architecture == FlashModuleSearch.ModuleArchitecture.X86 ? PluginDirectory32 : PluginDirectory;
            string target = Path.Combine(directory, name);
            // Module déjà installé choisi de nouveau : rien à copier.
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return null;
            try
            {
                Directory.CreateDirectory(directory);
                foreach (string previous in Directory.EnumerateFiles(directory).Where(IsModuleName).ToList())
                {
                    if (!string.Equals(Path.GetFileName(previous), name, StringComparison.OrdinalIgnoreCase))
                        File.Delete(previous);
                }
                File.Copy(source, target, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Après une recherche : installe le meilleur module de chaque architecture, s'il vaut mieux
        /// que celui déjà installé (voir <see cref="FlashModuleSearch.Best"/>). Retourne les modules
        /// installés et les erreurs.
        /// </summary>
        public static (IReadOnlyList<FlashModuleSearch.Module> Installed, IReadOnlyList<string> Errors) InstallBest(IReadOnlyList<FlashModuleSearch.Module> found)
        {
            var pool = new List<FlashModuleSearch.Module>();
            // Les modules en place d'abord : à égalité, ils restent.
            foreach ((string? path, bool is32Bit) in new[] { (InstalledModule, false), (InstalledModule32, true) })
            {
                if (path != null)
                    pool.Add(new FlashModuleSearch.Module(path, FlashModuleSearch.VersionOf(path), is32Bit));
            }
            pool.AddRange(found);

            var installed = new List<FlashModuleSearch.Module>();
            var errors = new List<string>();
            foreach (FlashModuleSearch.Module best in FlashModuleSearch.Best(pool))
            {
                if (best.Path == InstalledModule || best.Path == InstalledModule32)
                    continue;
                if (InstallModule(best.Path) is { } error)
                    errors.Add(Path.GetFileName(best.Path) + " : " + error);
                else
                    installed.Add(best);
            }
            return (installed, errors);
        }
    }
}
