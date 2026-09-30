using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Sous Linux, le moteur web (WebKitGTK 4.1) vient du système : la distribution le maintient
    /// à jour, correctifs de sécurité compris. Vérifié avant toute fenêtre, pour afficher un
    /// message clair plutôt qu'un plantage.
    /// </summary>
    public static class LinuxRequirements
    {
        sealed record Library(string File, string Name, string VersionPrefix, int Major, int Minor);

        static readonly Library[] Libraries =
        {
            new("libgtk-3.so.0", "GTK 3", "gtk", 3, 24),
            new("libwebkit2gtk-4.1.so.0", "WebKitGTK 4.1", "webkit", 2, 40)
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint VersionFunction();

        /// <summary>Vrai si tout est prêt ; sinon le problème est affiché.</summary>
        public static bool Check()
        {
            IReadOnlyList<string> problems = FindProblems();
            if (problems.Count == 0)
                return true;

            string message = BuildMessage(problems);
            Console.Error.WriteLine(message);
            ShowFallbackDialog(message);
            return false;
        }

        /// <summary>Bibliothèques absentes ou trop anciennes (liste vide : tout est prêt).</summary>
        public static IReadOnlyList<string> FindProblems()
        {
            var problems = new List<string>();
            foreach (Library library in Libraries)
            {
                if (!NativeLibrary.TryLoad(library.File, out IntPtr handle))
                {
                    problems.Add(Tr("{0} est absent", library.Name));
                    continue;
                }

                if (NativeLibrary.TryGetExport(handle, library.VersionPrefix + "_get_major_version", out IntPtr major) &&
                    NativeLibrary.TryGetExport(handle, library.VersionPrefix + "_get_minor_version", out IntPtr minor))
                {
                    uint v1 = Marshal.GetDelegateForFunctionPointer<VersionFunction>(major)();
                    uint v2 = Marshal.GetDelegateForFunctionPointer<VersionFunction>(minor)();
                    if (v1 < library.Major || (v1 == library.Major && v2 < library.Minor))
                        problems.Add(Tr("{0} {1}.{2} est trop ancien (version {3}.{4} au minimum)", library.Name, v1, v2, library.Major, library.Minor));
                }
            }
            return problems;
        }

        /// <summary>Commande d'installation adaptée à la distribution (d'après /etc/os-release).</summary>
        public static string InstallCommand(string? osRelease = null)
        {
            osRelease ??= ReadOsRelease();
            string ids = " " + (Field(osRelease, "ID") + " " + Field(osRelease, "ID_LIKE")).ToLowerInvariant() + " ";

            if (ids.Contains(" fedora ") || ids.Contains(" rhel "))
                return "sudo dnf install webkit2gtk4.1 gstreamer1-plugins-good";
            if (ids.Contains(" arch "))
                return "sudo pacman -S --needed webkit2gtk-4.1 gst-plugins-good";
            if (ids.Contains(" suse ") || ids.Contains(" opensuse "))
                return "sudo zypper install libwebkit2gtk-4_1-0 gstreamer-plugins-good";
            return "sudo apt install libwebkit2gtk-4.1-0 gstreamer1.0-plugins-good";
        }

        public static string BuildMessage(IReadOnlyList<string> problems, string? osRelease = null)
            => Tr("PommeBrowser utilise le moteur web fourni par votre système.") + "\n\n"
               + string.Join("\n", problems.Select(p => "• " + p)) + "\n\n"
               + Tr("Pour l'installer :") + "\n" + InstallCommand(osRelease) + "\n\n"
               + Tr("Distributions prises en charge : Ubuntu 22.04, Debian 12, Fedora 38, Linux Mint 21 et versions plus récentes, Arch, openSUSE Tumbleweed.");

        static string ReadOsRelease()
        {
            try
            {
                return File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        static string Field(string osRelease, string name)
        {
            foreach (string line in osRelease.Split('\n'))
            {
                if (line.StartsWith(name + "=", StringComparison.Ordinal))
                    return line[(name.Length + 1)..].Trim().Trim('"');
            }
            return string.Empty;
        }

        /// <summary>Sans moteur web, le message passe par zenity ou kdialog s'ils sont installés.</summary>
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
