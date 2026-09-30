using System;
using System.Diagnostics;
using System.IO;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Localization;
using MyHomelabBrowser.classes.Profiles;
using PommeBrowser.Linux.Core;
using PommeBrowser.Linux.Ui;

// WebKitGTK, GTK 4 et libadwaita : édition Linux uniquement.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace PommeBrowser.Linux
{
    static class Program
    {
        static int Main(string[] args)
        {
            RuntimeLogBuffer.Init();
            LinuxPaths.Initialize();

            // Dernier profil ouvert (même fichier profiles.json que l'édition Windows).
            var profiles = new ProfileService(AppDataContext.GlobalRoot);
            ProfileData.ApplyPendingMoves();
            ProfileData.RemoveOrphans(name =>
                profiles.ProfileExists(name) ||
                Directory.Exists(Path.Combine(AppDataContext.GlobalRoot, "profiles", name)));
            LinuxPaths.UseProfile(profiles.Current?.Username);

            AppearanceSettings appearance = LoadAppearance();
            InitializeLanguage(appearance.Language);

            var problems = SystemRequirements.FindProblems();
            if (problems.Count > 0)
            {
                string message = SystemRequirements.BuildMessage(problems);
                Console.Error.WriteLine(message);
                ShowFallbackDialog(message);
                return 1;
            }

            WebKit.Module.Initialize();
            Adw.Module.Initialize();

            var application = new BrowserApplication(appearance, profiles);
            return application.Run(args);
        }

        /// <summary>Premier lancement : langue du système (français ou anglais).</summary>
        static AppearanceSettings LoadAppearance()
        {
            if (File.Exists(AppearanceSettings.DefaultPath))
                return AppearanceSettings.Load();

            string? lang = Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all
                ? all
                : Environment.GetEnvironmentVariable("LANG");
            return new AppearanceSettings
            {
                Theme = AppTheme.System,
                Language = lang is { Length: > 0 } && !lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase) && lang != "C" && lang != "C.UTF-8" ? "en" : "fr"
            };
        }

        static void InitializeLanguage(string language)
        {
            if (language != "en")
                return;

            using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream("lang.en.json");
            Loc.Initialize("en", stream != null ? Loc.LoadTable(stream) : null);
        }

        /// <summary>Sans GTK, le message passe par zenity ou kdialog s'ils sont installés.</summary>
        static void ShowFallbackDialog(string message)
        {
            foreach ((string tool, string[] arguments) in new[]
                     {
                         ("zenity", new[] { "--error", "--no-markup", "--title=PommeBrowser", "--text=" + message }),
                         ("kdialog", new[] { "--title", "PommeBrowser", "--error", message })
                     })
            {
                try
                {
                    var start = new ProcessStartInfo(tool) { UseShellExecute = false };
                    foreach (string argument in arguments)
                        start.ArgumentList.Add(argument);
                    using Process? process = Process.Start(start);
                    process?.WaitForExit();
                    return;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Outil absent : essai du suivant.
                }
            }
        }
    }
}
