using System;
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
        public static string[] StartupUrls { get; private set; } = Array.Empty<string>();

        [STAThread]
        static int Main(string[] args)
        {
            // Installation, désinstallation et mises à jour Velopack (Windows) : avant tout le reste.
            if (OperatingSystem.IsWindows())
                Velopack.VelopackApp.Build().Run();

            args = Relauncher.WaitForPrevious(args);
            RuntimeLogBuffer.Init();
            AppPaths.Initialize();
            ErrorLog.InstallProcessHandlers();

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
            StartupUrls = args;

            if (OperatingSystem.IsLinux())
            {
                if (!LinuxRequirements.Check())
                    return 1;

                // Données et cache de WebKitGTK dans les dossiers du profil (voir AppPaths).
                Engine.Gtk.WebKitGtk.g_set_prgname(AppPaths.GlibProgramName);
            }

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
                Language = culture.Equals("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en"
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
