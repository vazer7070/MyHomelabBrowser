using Microsoft.Win32;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Tests;

/// <summary>
/// Navigateur par défaut : inscription auprès de Windows (Capabilities), fichier .desktop de
/// l'AppImage sous Linux. Les tests n'utilisent jamais le nom de la vraie inscription.
/// </summary>
public sealed class DefaultBrowserTests
{
    [Fact]
    public void The_desktop_file_of_the_appimage_launches_the_appimage()
    {
        const string template = "[Desktop Entry]\nType=Application\nName=PommeBrowser\nExec=pommebrowser %U\nTryExec=pommebrowser\nMimeType=text/html;x-scheme-handler/http;x-scheme-handler/https;\n";

        string desktop = DefaultBrowser.DesktopFile(template, "/home/léa/Applications/Pomme Browser $1.AppImage");

        Assert.Contains("Exec=\"/home/léa/Applications/Pomme Browser \\$1.AppImage\" %U\n", desktop);
        Assert.Contains("TryExec=/home/léa/Applications/Pomme Browser $1.AppImage\n", desktop);
        Assert.Contains("MimeType=text/html;x-scheme-handler/http;x-scheme-handler/https;", desktop);
        Assert.DoesNotContain("Exec=pommebrowser", desktop);
        Assert.Single(desktop.Split('\n'), line => line.StartsWith("TryExec=", StringComparison.Ordinal));
        Assert.StartsWith("[Desktop Entry]\nTryExec=", desktop);
    }

    [Fact]
    public void Without_template_a_minimal_browser_entry_is_written()
    {
        string desktop = DefaultBrowser.DesktopFile(null, "/opt/PommeBrowser.AppImage");

        Assert.StartsWith("[Desktop Entry]\n", desktop);
        Assert.Contains("Exec=\"/opt/PommeBrowser.AppImage\" %U", desktop);
        Assert.Contains("Categories=Network;WebBrowser;", desktop);
        Assert.Contains("x-scheme-handler/https", desktop);
        Assert.Contains("Icon=" + LinuxPaths.AppId, desktop);
    }

    [Theory]
    [InlineData("/a/b", "\"/a/b\"")]
    [InlineData("/a b/\"c\"", "\"/a b/\\\"c\\\"\"")]
    [InlineData("/x/`y`$z\\w", "\"/x/\\`y\\`\\$z\\\\w\"")]
    public void Exec_arguments_are_quoted_as_the_specification_says(string value, string expected)
        => Assert.Equal(expected, DefaultBrowser.DesktopQuote(value));

    [Fact]
    public void Installing_the_appimage_entry_writes_the_desktop_file_and_its_icon()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string root = Path.Combine(Path.GetTempPath(), "pomme-desktop-" + Guid.NewGuid().ToString("N")[..8]);
        string? previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            string appDir = Path.Combine(root, "AppDir");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, LinuxPaths.AppId + ".desktop"), "[Desktop Entry]\nName=PommeBrowser\nExec=pommebrowser %U\n");
            File.WriteAllBytes(Path.Combine(appDir, LinuxPaths.AppId + ".png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));

            DefaultBrowser.InstallAppImageDesktopFile("/opt/PommeBrowser.AppImage", appDir);

            string desktop = File.ReadAllText(Path.Combine(root, "data", "applications", LinuxPaths.AppId + ".desktop"));
            Assert.Contains("Exec=\"/opt/PommeBrowser.AppImage\" %U", desktop);
            Assert.True(File.Exists(Path.Combine(root, "data", "icons", "hicolor", "256x256", "apps", LinuxPaths.AppId + ".png")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Windows_lists_the_browser_after_registration_and_forgets_it_after_removal()
    {
        if (!OperatingSystem.IsWindows())
            return;
        const string name = "PommeBrowserTest";
        const string progId = "PommeBrowserTestHTML";
        const string exe = @"C:\Program Files\Pomme Test\MyHomelabBrowser.exe";
        try
        {
            DefaultBrowser.Register(exe, name, progId);

            using (RegistryKey? command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + progId + @"\shell\open\command"))
                Assert.Equal("\"" + exe + "\" \"%1\"", command?.GetValue(null));
            using (RegistryKey? urls = Registry.CurrentUser.OpenSubKey(@"Software\Clients\StartMenuInternet\" + name + @"\Capabilities\URLAssociations"))
            {
                Assert.Equal(progId, urls?.GetValue("http"));
                Assert.Equal(progId, urls?.GetValue("https"));
            }
            using (RegistryKey? files = Registry.CurrentUser.OpenSubKey(@"Software\Clients\StartMenuInternet\" + name + @"\Capabilities\FileAssociations"))
                Assert.Equal(progId, files?.GetValue(".html"));
            using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications"))
                Assert.Equal(@"Software\Clients\StartMenuInternet\" + name + @"\Capabilities", registered?.GetValue(name));

            DefaultBrowser.Unregister(name, progId);

            Assert.Null(Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + progId));
            Assert.Null(Registry.CurrentUser.OpenSubKey(@"Software\Clients\StartMenuInternet\" + name));
            using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications"))
                Assert.Null(registered?.GetValue(name));
            using (RegistryKey? openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.html\OpenWithProgids"))
                Assert.DoesNotContain(progId, openWith?.GetValueNames() ?? Array.Empty<string>());
        }
        finally
        {
            DefaultBrowser.Unregister(name, progId);
        }
    }
}
