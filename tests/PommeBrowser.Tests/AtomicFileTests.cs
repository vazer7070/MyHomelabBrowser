using MyHomelabBrowser.classes;

namespace PommeBrowser.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pomme-atomic-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void Writes_replace_the_file_and_leave_no_temporary_file()
    {
        string path = Path.Combine(_folder, "sub", "data.json");

        AtomicFile.WriteAllText(path, "{\"v\":1}");
        AtomicFile.WriteAllText(path, "{\"v\":2}");

        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
}
