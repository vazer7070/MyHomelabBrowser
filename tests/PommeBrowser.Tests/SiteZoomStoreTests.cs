using MyHomelabBrowser.classes;

namespace PommeBrowser.Tests;

public sealed class SiteZoomStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pomme-zoom-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void Zoom_is_remembered_per_host_and_port()
    {
        string path = Path.Combine(_folder, "zoom.json");
        var store = new SiteZoomStore(() => path);

        store.Set(new Uri("http://192.168.1.10:8080/app"), 1.25);

        Assert.Equal(1.25, store.Get(new Uri("http://192.168.1.10:8080/autre")));
        Assert.Equal(1.0, store.Get(new Uri("http://192.168.1.10:9000/")));

        // Relu depuis le disque par une nouvelle instance.
        Assert.Equal(1.25, new SiteZoomStore(() => path).Get(new Uri("http://192.168.1.10:8080/")));
    }

    [Fact]
    public void Default_zoom_removes_the_entry_and_values_are_clamped()
    {
        string path = Path.Combine(_folder, "zoom.json");
        var store = new SiteZoomStore(() => path);
        var uri = new Uri("https://example.com");

        store.Set(uri, 9);
        Assert.Equal(SiteZoomStore.MaximumZoom, store.Get(uri));

        store.Set(uri, 1.0);
        Assert.DoesNotContain("example.com", File.ReadAllText(path));
    }

    [Fact]
    public void Non_web_addresses_are_not_remembered()
    {
        Assert.Null(SiteZoomStore.KeyFor(new Uri("about:blank")));
        Assert.Null(SiteZoomStore.KeyFor(new Uri("file:///C:/test.html")));
    }

    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(1.17, 1, 1.25)]
    [InlineData(1.17, -1, 1.1)]
    [InlineData(5.0, 1, 5.0)]
    [InlineData(0.25, -1, 0.25)]
    public void Steps_follow_the_usual_levels(double current, int direction, double expected)
        => Assert.Equal(expected, SiteZoomStore.Step(current, direction));
}
