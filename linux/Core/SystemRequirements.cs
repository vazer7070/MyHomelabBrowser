using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Le moteur web (WebKitGTK) et l'interface (GTK 4, libadwaita) viennent du système :
    /// la distribution les maintient à jour, correctifs de sécurité compris.
    /// Vérifié avant toute fenêtre, pour afficher un message clair plutôt qu'un plantage.
    /// </summary>
    public static class SystemRequirements
    {
        sealed record Library(string File, string Name, string VersionPrefix, int Major, int Minor);

        static readonly Library[] Libraries =
        {
            new("libgtk-4.so.1", "GTK 4", "gtk", 4, 12),
            new("libadwaita-1.so.0", "libadwaita", "adw", 1, 5),
            new("libwebkitgtk-6.0.so.4", "WebKitGTK 6.0", "webkit", 2, 40)
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint VersionFunction();

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

                (uint major, uint minor)? version = ReadVersion(handle, library.VersionPrefix);
                if (version is { } v && (v.major < library.Major || (v.major == library.Major && v.minor < library.Minor)))
                    problems.Add(Tr("{0} {1}.{2} est trop ancien (version {3}.{4} au minimum)", library.Name, v.major, v.minor, library.Major, library.Minor));
            }
            return problems;
        }

        static (uint, uint)? ReadVersion(IntPtr handle, string prefix)
        {
            if (!NativeLibrary.TryGetExport(handle, prefix + "_get_major_version", out IntPtr major) ||
                !NativeLibrary.TryGetExport(handle, prefix + "_get_minor_version", out IntPtr minor))
            {
                return null;
            }

            return (Marshal.GetDelegateForFunctionPointer<VersionFunction>(major)(),
                    Marshal.GetDelegateForFunctionPointer<VersionFunction>(minor)());
        }

        /// <summary>Commande d'installation adaptée à la distribution (d'après /etc/os-release).</summary>
        public static string InstallCommand(string? osRelease = null)
        {
            osRelease ??= ReadOsRelease();
            string ids = " " + (Field(osRelease, "ID") + " " + Field(osRelease, "ID_LIKE")).ToLowerInvariant() + " ";

            if (ids.Contains(" fedora ") || ids.Contains(" rhel "))
                return "sudo dnf install webkitgtk6.0 libadwaita gstreamer1-plugins-good";
            if (ids.Contains(" arch "))
                return "sudo pacman -S --needed webkitgtk-6.0 libadwaita gst-plugins-good";
            if (ids.Contains(" suse ") || ids.Contains(" opensuse "))
                return "sudo zypper install libwebkitgtk-6_0-4 libadwaita-1-0 gstreamer-plugins-good";
            return "sudo apt install libwebkitgtk-6.0-4 libadwaita-1-0 gstreamer1.0-plugins-good";
        }

        public static string BuildMessage(IReadOnlyList<string> problems, string? osRelease = null)
            => Tr("PommeBrowser utilise le moteur web et l'interface fournis par votre système.") + "\n\n"
               + string.Join("\n", problems.Select(p => "• " + p)) + "\n\n"
               + Tr("Pour les installer :") + "\n" + InstallCommand(osRelease) + "\n\n"
               + Tr("Distributions prises en charge : Ubuntu 24.04, Debian 13, Fedora 40, Linux Mint 22 et versions plus récentes, Arch, openSUSE Tumbleweed.");

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
    }
}
