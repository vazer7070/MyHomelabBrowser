using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Core
{
    /// <summary>
    /// PommeBrowser comme navigateur par défaut, pour que les liens des autres applications
    /// (courriels, documents, messageries) s'y ouvrent, comme avec Firefox ou Chrome.
    /// - Windows : PommeBrowser s'inscrit comme navigateur (Capabilities, à l'installation et à
    ///   chaque mise à jour) ; depuis Windows 10, le choix revient à l'utilisateur, dans la page
    ///   « Applications par défaut », ouverte à son nom.
    /// - Linux : fichier .desktop de l'utilisateur (AppImage) puis xdg-settings.
    /// - macOS : http et https déclarés dans Info.plist ; le système demande confirmation.
    /// </summary>
    public static class DefaultBrowser
    {
        public const string RegistrationName = "PommeBrowser";
        const string ProgId = "PommeBrowserHTML";
        static readonly string[] FileExtensions = { ".htm", ".html", ".shtml", ".xht", ".xhtml" };
        static readonly string[] Schemes = { "http", "https" };

        /// <summary>PommeBrowser ouvre déjà les liens ; null si le système ne permet pas de le savoir.</summary>
        public static Task<bool?> IsDefaultAsync() => Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return (bool?)string.Equals(WindowsUserChoice("https"), ProgId, StringComparison.OrdinalIgnoreCase);
                if (OperatingSystem.IsLinux())
                    return Run("xdg-settings", "get", "default-web-browser") is { } current ? current.Trim() == DesktopFileName : null;
                if (OperatingSystem.IsMacOS())
                    return MacIsDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException)
            {
                RuntimeLogBuffer.Append("[Navigateur par défaut] " + ex.Message);
            }
            return null;
        });

        /// <summary>
        /// Demande à devenir le navigateur par défaut. Windows : la page « Applications par défaut »
        /// s'ouvre (l'utilisateur choisit) ; Linux et macOS : réglé aussitôt (macOS demande
        /// confirmation). Message d'erreur, ou null.
        /// </summary>
        public static Task<string?> MakeDefaultAsync() => Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return WindowsMakeDefault();
                if (OperatingSystem.IsLinux())
                    return LinuxMakeDefault();
                if (OperatingSystem.IsMacOS())
                    return MacMakeDefault();
                return Tr("Ce système ne permet pas de choisir le navigateur par défaut depuis PommeBrowser.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                           System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                RuntimeLogBuffer.Append("[Navigateur par défaut] " + ex.Message);
                return ex.Message;
            }
        });

        // ---------------------------------------------------------------
        // Windows
        // ---------------------------------------------------------------

        /// <summary>
        /// Lanceur stable de l'installation Velopack (dossier racine, au-dessus de « current ») :
        /// le chemin inscrit reste valable après les mises à jour. Null hors installation (essai, portable).
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static string? InstalledLauncher()
        {
            string? exe = Environment.ProcessPath;
            string? directory = exe != null ? Path.GetDirectoryName(exe) : null;
            if (exe == null || directory == null || !string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase))
                return null;
            string launcher = Path.Combine(Path.GetDirectoryName(directory)!, Path.GetFileName(exe));
            return File.Exists(launcher) ? launcher : null;
        }

        /// <summary>
        /// Inscription comme navigateur (utilisateur courant) : Windows le propose ensuite pour les
        /// liens (http, https) et les pages (.html…). Sans effet sur le choix actuel de l'utilisateur.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void Register(string executable) => Register(executable, RegistrationName, ProgId);

        /// <summary>Inscription sous un autre nom (tests : sans toucher à celle de PommeBrowser).</summary>
        [SupportedOSPlatform("windows")]
        internal static void Register(string executable, string name, string progIdName)
        {
            string open = "\"" + executable + "\" \"%1\"";
            string icon = "\"" + executable + "\",0";
            using (RegistryKey progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progIdName))
            {
                progId.SetValue(null, "PommeBrowser HTML");
                progId.SetValue("URL Protocol", string.Empty);
                using (RegistryKey application = progId.CreateSubKey("Application"))
                {
                    application.SetValue("ApplicationName", name);
                    application.SetValue("ApplicationIcon", icon);
                }
                using (RegistryKey defaultIcon = progId.CreateSubKey("DefaultIcon"))
                    defaultIcon.SetValue(null, icon);
                using (RegistryKey command = progId.CreateSubKey(@"shell\open\command"))
                    command.SetValue(null, open);
            }

            string clientPath = @"Software\Clients\StartMenuInternet\" + name;
            using (RegistryKey client = Registry.CurrentUser.CreateSubKey(clientPath))
            {
                client.SetValue(null, name);
                using (RegistryKey capabilities = client.CreateSubKey("Capabilities"))
                {
                    capabilities.SetValue("ApplicationName", name);
                    capabilities.SetValue("ApplicationDescription", Tr("Navigateur web pour le homelab, avec Flash"));
                    capabilities.SetValue("ApplicationIcon", icon);
                    using (RegistryKey files = capabilities.CreateSubKey("FileAssociations"))
                    {
                        foreach (string extension in FileExtensions)
                            files.SetValue(extension, progIdName);
                    }
                    using (RegistryKey urls = capabilities.CreateSubKey("URLAssociations"))
                    {
                        foreach (string scheme in Schemes)
                            urls.SetValue(scheme, progIdName);
                    }
                    using (RegistryKey startMenu = capabilities.CreateSubKey("StartMenu"))
                        startMenu.SetValue("StartMenuInternet", name);
                }
                using (RegistryKey defaultIcon = client.CreateSubKey("DefaultIcon"))
                    defaultIcon.SetValue(null, icon);
                using (RegistryKey command = client.CreateSubKey(@"shell\open\command"))
                    command.SetValue(null, "\"" + executable + "\"");
            }

            using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                registered.SetValue(name, clientPath + @"\Capabilities");
            // « Ouvrir avec » des pages enregistrées.
            foreach (string extension in FileExtensions)
            {
                using RegistryKey openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
                openWith.SetValue(progIdName, Array.Empty<byte>(), RegistryValueKind.None);
            }
            NotifyAssociationsChanged();
        }

        /// <summary>Désinscription (désinstallation) : Windows reprend son navigateur précédent.</summary>
        [SupportedOSPlatform("windows")]
        public static void Unregister() => Unregister(RegistrationName, ProgId);

        [SupportedOSPlatform("windows")]
        internal static void Unregister(string name, string progIdName)
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + progIdName, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Clients\StartMenuInternet\" + name, throwOnMissingSubKey: false);
            using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                registered?.DeleteValue(name, throwOnMissingValue: false);
            foreach (string extension in FileExtensions)
            {
                using RegistryKey? openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", writable: true);
                openWith?.DeleteValue(progIdName, throwOnMissingValue: false);
            }
            NotifyAssociationsChanged();
        }

        /// <summary>Inscrit d'après une installation antérieure à cette fonction : rien à faire si c'est déjà le cas.</summary>
        [SupportedOSPlatform("windows")]
        public static void EnsureRegistered()
        {
            if (InstalledLauncher() is not { } launcher)
                return;
            using RegistryKey? command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command");
            if (command?.GetValue(null) is string current && current.StartsWith("\"" + launcher + "\"", StringComparison.OrdinalIgnoreCase))
                return;
            Register(launcher);
        }

        [SupportedOSPlatform("windows")]
        static string? WindowsMakeDefault()
        {
            // Inscription d'abord (essai hors installation compris), puis le choix de l'utilisateur.
            Register(InstalledLauncher() ?? Environment.ProcessPath ?? throw new IOException(Tr("Chemin du programme inconnu.")));
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + RegistrationName) { UseShellExecute = true })?.Dispose();
            return null;
        }

        [SupportedOSPlatform("windows")]
        static string? WindowsUserChoice(string scheme)
        {
            using RegistryKey? choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + scheme + @"\UserChoice");
            return choice?.GetValue("ProgId") as string;
        }

        [SupportedOSPlatform("windows")]
        static void NotifyAssociationsChanged() => SHChangeNotify(0x08000000, 0, 0, 0);

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

        // ---------------------------------------------------------------
        // Linux
        // ---------------------------------------------------------------

        static string DesktopFileName => LinuxPaths.AppId + ".desktop";

        static string? LinuxMakeDefault()
        {
            // AppImage : le système ne le connaît pas ; un fichier .desktop de l'utilisateur le présente.
            if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage))
                InstallAppImageDesktopFile(appImage, Environment.GetEnvironmentVariable("APPDIR"));

            if (Run("xdg-settings", "set", "default-web-browser", DesktopFileName) != null)
                return null;
            // Bureaux sans xdg-settings complet : associations MIME seules.
            if (Run("xdg-mime", "default", DesktopFileName, "x-scheme-handler/http", "x-scheme-handler/https", "text/html", "application/xhtml+xml") != null)
                return null;
            return Tr("Le système n'a pas accepté le changement (xdg-settings). Choisissez PommeBrowser dans les réglages de votre bureau (Applications par défaut).");
        }

        /// <summary>
        /// Fichier .desktop de l'utilisateur pour l'AppImage (comme le ferait une intégration au
        /// bureau) : repris de celui de l'AppImage, lancé par le chemin de l'AppImage, avec son icône.
        /// </summary>
        public static void InstallAppImageDesktopFile(string appImage, string? appDir)
        {
            string applications = Path.Combine(DataHome(), "applications");
            Directory.CreateDirectory(applications);

            string? template = appDir != null && File.Exists(Path.Combine(appDir, DesktopFileName))
                ? File.ReadAllText(Path.Combine(appDir, DesktopFileName))
                : null;
            File.WriteAllText(Path.Combine(applications, DesktopFileName), DesktopFile(template, appImage));

            string? icon = appDir != null ? Path.Combine(appDir, LinuxPaths.AppId + ".png") : null;
            if (icon != null && File.Exists(icon))
            {
                string icons = Path.Combine(DataHome(), "icons", "hicolor", "256x256", "apps");
                Directory.CreateDirectory(icons);
                File.Copy(icon, Path.Combine(icons, LinuxPaths.AppId + ".png"), overwrite: true);
            }
            Run("update-desktop-database", applications);
        }

        /// <summary>Dossier de données XDG de l'utilisateur (applications et icônes du bureau), pas celui de PommeBrowser.</summary>
        static string DataHome()
            => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } value && Path.IsPathRooted(value)
                ? value
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        /// <summary>
        /// Contenu du fichier .desktop : celui de l'AppImage (noms traduits, catégories, types
        /// MIME) avec Exec et TryExec pointant sur l'AppImage ; un fichier minimal sinon.
        /// </summary>
        public static string DesktopFile(string? template, string appImage)
        {
            string exec = "Exec=" + DesktopQuote(appImage) + " %U";
            if (template == null)
            {
                return string.Join('\n',
                    "[Desktop Entry]", "Type=Application", "Name=PommeBrowser", "GenericName=Navigateur web",
                    exec, "TryExec=" + appImage, "Icon=" + LinuxPaths.AppId, "Terminal=false", "StartupNotify=true",
                    "Categories=Network;WebBrowser;",
                    "MimeType=text/html;application/xhtml+xml;x-scheme-handler/http;x-scheme-handler/https;", string.Empty);
            }

            var lines = template.Replace("\r\n", "\n").Split('\n')
                .Where(line => !line.StartsWith("TryExec=", StringComparison.Ordinal))
                .Select(line => line.StartsWith("Exec=", StringComparison.Ordinal) ? exec : line)
                .ToList();
            int entry = lines.FindIndex(line => line.Trim() == "[Desktop Entry]");
            lines.Insert(entry + 1, "TryExec=" + appImage);
            return string.Join('\n', lines).TrimEnd('\n') + "\n";
        }

        /// <summary>Argument d'une ligne Exec (spécification des fichiers .desktop) : entre guillemets, caractères réservés échappés.</summary>
        public static string DesktopQuote(string value)
        {
            var quoted = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c is '"' or '`' or '$' or '\\')
                    quoted.Append('\\');
                quoted.Append(c);
            }
            return quoted.Append('"').ToString();
        }

        /// <summary>Commande du système ; sa sortie si elle réussit, null sinon (absente, échec, délai).</summary>
        static string? Run(string program, params string[] arguments)
        {
            var start = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            try
            {
                using Process? process = Process.Start(start);
                if (process == null)
                    return null;
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    return null;
                }
                return process.ExitCode == 0 ? output.Result : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------
        // macOS
        // ---------------------------------------------------------------

        [SupportedOSPlatform("macos")]
        static string? MacMakeDefault()
        {
            if (MacBundleIdentifier() is not { } bundle)
                return Tr("PommeBrowser doit être lancé depuis son application (PommeBrowser.app) pour devenir le navigateur par défaut.");
            foreach (string scheme in Schemes)
            {
                nint schemeRef = CFString(scheme);
                nint bundleRef = CFString(bundle);
                try
                {
                    int status = LSSetDefaultHandlerForURLScheme(schemeRef, bundleRef);
                    if (status != 0)
                        return Tr("Le système n'a pas accepté le changement (erreur {0}).", status);
                }
                finally
                {
                    CFRelease(schemeRef);
                    CFRelease(bundleRef);
                }
            }
            return null;
        }

        [SupportedOSPlatform("macos")]
        static bool? MacIsDefault()
        {
            if (MacBundleIdentifier() is not { } bundle)
                return null;
            nint scheme = CFString("https");
            try
            {
                nint handler = LSCopyDefaultHandlerForURLScheme(scheme);
                if (handler == 0)
                    return false;
                try
                {
                    return string.Equals(FromCFString(handler), bundle, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    CFRelease(handler);
                }
            }
            finally
            {
                CFRelease(scheme);
            }
        }

        [SupportedOSPlatform("macos")]
        static string? MacBundleIdentifier()
        {
            nint bundle = CFBundleGetMainBundle();
            nint identifier = bundle != 0 ? CFBundleGetIdentifier(bundle) : 0;
            return identifier != 0 ? FromCFString(identifier) : null;
        }

        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
        const uint Utf8Encoding = 0x08000100;

        static nint CFString(string value) => CFStringCreateWithCString(0, value, Utf8Encoding);

        static string? FromCFString(nint value)
        {
            var buffer = new byte[1024];
            return CFStringGetCString(value, buffer, buffer.Length, Utf8Encoding)
                ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length)
                : null;
        }

        [DllImport(CoreFoundation)] static extern nint CFBundleGetMainBundle();
        [DllImport(CoreFoundation)] static extern nint CFBundleGetIdentifier(nint bundle);
        [DllImport(CoreFoundation)] static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CFStringGetCString(nint value, byte[] buffer, nint size, uint encoding);
        [DllImport(CoreFoundation)] static extern void CFRelease(nint value);
        [DllImport(CoreServices)] static extern int LSSetDefaultHandlerForURLScheme(nint scheme, nint bundleId);
        [DllImport(CoreServices)] static extern nint LSCopyDefaultHandlerForURLScheme(nint scheme);
    }
}
