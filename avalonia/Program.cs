using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Localization;
using MyHomelabBrowser.classes.Profiles;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;

namespace PommeBrowser
{
    static class Program
    {
        /// <summary>Profils (même fichier profiles.json que les autres éditions), lus avant toute fenêtre.</summary>
        public static ProfileService Profiles { get; private set; } = null!;

        public static AppearanceSettings Appearance { get; private set; } = new();

        /// <summary>Lancement du programme (durée du démarrage, dans le journal).</summary>
        public static readonly long StartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>Adresses passées en ligne de commande (ouvertes dans des onglets).</summary>
        public static IReadOnlyList<string> StartupUrls { get; private set; } = Array.Empty<string>();

        /// <summary>Instance unique : reçoit les adresses des lancements suivants (null dans les tests).</summary>
        public static SingleInstance? Instance { get; private set; }

        [STAThread]
        static int Main(string[] args)
        {
            // Installation, désinstallation et mises à jour Velopack (Windows) : avant tout le reste.
            // PommeBrowser s'inscrit comme navigateur (Windows le propose ensuite pour les liens) à
            // l'installation et à chaque mise à jour, et se retire à la désinstallation.
            if (OperatingSystem.IsWindows())
            {
                // Une version téléchargée n'est pas installée d'office au démarrage : elle l'est à la
                // fermeture (BrowserApp.OnProcessEnding), et jamais sur une compilation de test.
                Velopack.VelopackApp.Build()
                    .SetAutoApplyOnStartup(false)
                    .OnAfterInstallFastCallback(_ => RegisterAsBrowser())
                    .OnAfterUpdateFastCallback(_ => RegisterAsBrowser())
                    .OnBeforeUninstallFastCallback(_ => SafeRegistry(() =>
                    {
                        if (OperatingSystem.IsWindows())
                            DefaultBrowser.Unregister();
                    }))
                    .Run();
            }

            args = Relauncher.WaitForPrevious(args);
            RuntimeLogBuffer.Init();
            AppPaths.Initialize();
            ErrorLog.InstallProcessHandlers();

            // PommeBrowser déjà ouvert (lien cliqué dans une autre application, icône…) : il
            // reçoit les adresses et ouvre les onglets ; ce lancement s'arrête là.
            IReadOnlyList<string> targets = LaunchTargets.Resolve(args, Environment.CurrentDirectory);
            Instance = SingleInstance.Claim(SingleInstance.ChannelName(AppDataContext.GlobalRoot), targets, RuntimeLogBuffer.Append);
            if (Instance == null)
                return 0;
            // Seule instance : un arrêt brutal de la session précédente est consigné (errors.log).
            CrashWatch.Start();
            // Installation antérieure à l'inscription comme navigateur : faite maintenant, sans attendre.
            if (OperatingSystem.IsWindows())
                _ = System.Threading.Tasks.Task.Run(() => SafeRegistry(() =>
                {
                    if (OperatingSystem.IsWindows())
                        DefaultBrowser.EnsureRegistered();
                }));

            // Dernier profil ouvert. Les données d'un profil renommé ou supprimé pendant que
            // PommeBrowser tournait sont déplacées ou effacées maintenant, moteur arrêté.
            Profiles = new ProfileService(AppDataContext.GlobalRoot);
            if (OperatingSystem.IsWindows())
            {
                WebViewProfileData.ApplyPendingOperations();
            }
            else if (OperatingSystem.IsLinux())
            {
                ProfileData.ApplyPendingMoves();
                ProfileData.RemoveOrphans(name =>
                    Profiles.ProfileExists(name) ||
                    Directory.Exists(Path.Combine(AppDataContext.GlobalRoot, "profiles", name)));
            }
            AppPaths.UseProfile(Profiles.Current?.Username);

            Appearance = LoadAppearance();
            InitializeLanguage(Appearance.Language);
            StartupUrls = targets;

            if (OperatingSystem.IsLinux())
            {
                if (!LinuxRequirements.Check())
                    return 1;

                // Données et cache de WebKitGTK dans les dossiers du profil (voir AppPaths).
                Engine.Gtk.WebKitGtk.g_set_prgname(AppPaths.GlibProgramName);
            }

            int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            BrowserApp.Current?.OnProcessEnding();
            return code;
        }

        static void RegisterAsBrowser()
        {
            if (OperatingSystem.IsWindows() && DefaultBrowser.InstalledLauncher() is { } launcher)
                SafeRegistry(() =>
                {
                    if (OperatingSystem.IsWindows())
                        DefaultBrowser.Register(launcher);
                });
        }

        /// <summary>Inscription dans le registre : un échec n'empêche ni l'installation ni le démarrage.</summary>
        static void SafeRegistry(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                RuntimeLogBuffer.Append("[Navigateur par défaut] " + ex.Message);
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new X11PlatformOptions { WmClass = LinuxPaths.AppId })
                .LogToTrace();

        /// <summary>Premier lancement : langue du système (français ou anglais), thème du système.</summary>
        static AppearanceSettings LoadAppearance()
        {
            if (File.Exists(AppearanceSettings.DefaultPath))
                return AppearanceSettings.Load();

            string culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (OperatingSystem.IsLinux())
            {
                string? lang = Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all
                    ? all
                    : Environment.GetEnvironmentVariable("LANG");
                culture = lang is { Length: >= 2 } && lang != "C" && !lang.StartsWith("C.", StringComparison.Ordinal) ? lang[..2] : "fr";
            }

            return new AppearanceSettings
            {
                Theme = AppTheme.System,
                Language = culture.Equals("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en",
                WelcomeDone = false
            };
        }

        static void InitializeLanguage(string language)
        {
            if (language != "en")
                return;

            using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream("lang.en.json");
            Loc.Initialize("en", stream != null ? Loc.LoadTable(stream) : null);
        }
    }
}
