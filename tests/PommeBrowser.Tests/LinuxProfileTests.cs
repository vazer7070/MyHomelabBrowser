using PommeBrowser.Linux.Core;

namespace PommeBrowser.Tests;

/// <summary>Profils de l'édition Linux : dossiers par profil et relance de l'application.</summary>
public sealed class LinuxProfileTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "pomme-profiles-" + Guid.NewGuid().ToString("N"));

    public LinuxProfileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheDefaultProfileKeepsTheBaseDirectories()
    {
        Assert.Equal("/data", LinuxPaths.ProfileDirectory("/data", null));
        Assert.Equal("/data", LinuxPaths.ProfileDirectory("/data", "  "));
    }

    [Fact]
    public void ACreatedProfileHasItsOwnDirectoryNamedInLowerCase()
        => Assert.Equal(Path.Combine("/data", "profiles", "alice"), LinuxPaths.ProfileDirectory("/data", " Alice "));

    [Fact]
    public void OrphansAreTheDirectoriesOfProfilesThatNoLongerExist()
    {
        string profiles = Path.Combine(_directory, "profiles");
        Directory.CreateDirectory(Path.Combine(profiles, "alice"));
        Directory.CreateDirectory(Path.Combine(profiles, "bob"));

        IReadOnlyList<string> orphans = ProfileData.FindOrphans(_directory, name => name == "alice");

        Assert.Equal(new[] { Path.Combine(profiles, "bob") }, orphans);
        Assert.Empty(ProfileData.FindOrphans(Path.Combine(_directory, "absent"), _ => false));
    }

    [Fact]
    public void TheAppImageIsRelaunchedRatherThanItsTemporaryMount()
    {
        IReadOnlyList<string> command = AppRestart.RelaunchCommand(
            "/home/u/Apps/PommeBrowser.AppImage", "/tmp/.mount_Pomme/usr/lib/pommebrowser/pommebrowser", null);

        Assert.Equal(new[] { "/home/u/Apps/PommeBrowser.AppImage" }, command);
    }

    [Fact]
    public void OutsideAnAppImageTheExecutableIsRelaunched()
    {
        Assert.Equal(new[] { "/opt/pomme/pommebrowser" }, AppRestart.RelaunchCommand(null, "/opt/pomme/pommebrowser", "/opt/pomme/pommebrowser.dll"));
        Assert.Equal(new[] { "/usr/lib/dotnet/dotnet", "/src/pommebrowser.dll" }, AppRestart.RelaunchCommand("", "/usr/lib/dotnet/dotnet", "/src/pommebrowser.dll"));
        Assert.Throws<InvalidOperationException>(() => AppRestart.RelaunchCommand(null, null, null));
    }

    [Fact]
    public void TheHelperWaitsForTheCurrentProcessThenStartsTheCommand()
    {
        System.Diagnostics.ProcessStartInfo start = AppRestart.HelperStartInfo(4242, new[] { "/apps/Pomme Browser.AppImage" });

        Assert.Equal("/bin/sh", start.FileName);
        Assert.Equal(new[] { "-c", AppRestart.WaitScript, "pommebrowser-restart", "4242", "/apps/Pomme Browser.AppImage" }, start.ArgumentList);
    }

    [LinuxFact]
    public void TheWaitScriptRunsTheCommandOnceTheProcessHasExited()
    {
        string marker = Path.Combine(_directory, "relaunched");
        using var sleeper = System.Diagnostics.Process.Start("sleep", "0.5")!;
        using var helper = System.Diagnostics.Process.Start(AppRestart.HelperStartInfo(sleeper.Id, new[] { "touch", marker }))!;

        Assert.True(helper.WaitForExit(10_000));
        Assert.True(sleeper.HasExited);
        Assert.True(File.Exists(marker));
    }
}
