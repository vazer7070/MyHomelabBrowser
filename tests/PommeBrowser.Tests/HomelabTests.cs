using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Security;
using MyHomelabBrowser.classes.Workspaces;
using System.Net;

namespace PommeBrowser.Tests;

public sealed class HomelabTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pomme-homelab-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private string PathFor(string file) => Path.Combine(_folder, file);

    [Fact]
    public void Services_are_persisted_and_validated()
    {
        var store = new HomelabServiceStore(() => PathFor("services.json"));
        var nas = new HomelabService { Name = " NAS ", Url = "https://nas.lan:5001/", Group = " Stockage " };

        store.AddOrUpdate(nas);
        Assert.Throws<ArgumentException>(() => store.AddOrUpdate(new HomelabService { Name = "x", Url = "nas.lan" }));

        var reloaded = new HomelabServiceStore(() => PathFor("services.json")).GetAll();
        Assert.Single(reloaded);
        Assert.Equal("NAS", reloaded[0].Name);
        Assert.Equal("Stockage", reloaded[0].Group);

        int added = store.AddRange(new[]
        {
            new HomelabService { Name = "Doublon", Url = "https://nas.lan:5001" },
            new HomelabService { Name = "Proxmox", Url = "https://pve.lan:8006" }
        });
        Assert.Equal(1, added);
        Assert.True(store.Remove(nas.Id));
        Assert.Single(store.GetAll());
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Respond(request));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ServiceState.Online)]
    [InlineData(HttpStatusCode.Unauthorized, ServiceState.Online)]
    [InlineData(HttpStatusCode.Found, ServiceState.Online)]
    [InlineData(HttpStatusCode.BadGateway, ServiceState.Degraded)]
    public async Task Any_http_answer_means_the_service_runs(HttpStatusCode code, ServiceState expected)
    {
        var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(code) };
        using var checker = new ServiceHealthChecker(handler: handler);

        ServiceCheckResult result = await checker.CheckAsync("http://nas.lan");

        Assert.Equal(expected, result.State);
        Assert.Equal((int)code, result.StatusCode);
    }

    [Fact]
    public async Task Network_errors_mean_offline()
    {
        var handler = new FakeHandler { Respond = _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refusé") };
        using var checker = new ServiceHealthChecker(handler: handler);

        ServiceCheckResult result = await checker.CheckAsync("http://nas.lan");

        Assert.Equal(ServiceState.Offline, result.State);
        Assert.Equal("injoignable", result.Error);
    }

    [Fact]
    public async Task Monitor_reports_transitions_but_not_the_first_result()
    {
        bool up = true;
        var handler = new FakeHandler
        {
            Respond = _ => up ? new HttpResponseMessage(HttpStatusCode.OK) : throw new HttpRequestException(HttpRequestError.ConnectionError)
        };
        var service = new HomelabService { Name = "NAS", Url = "http://nas.lan" };
        using var monitor = new ServiceMonitor(() => new[] { service }, new ServiceHealthChecker(handler: handler));

        var transitions = new List<(ServiceState From, ServiceState To)>();
        monitor.StateChanged += (_, from, to) => transitions.Add((from, to));

        await monitor.CheckAllAsync();
        Assert.Empty(transitions);

        up = false;
        await monitor.CheckAllAsync();
        up = true;
        await monitor.CheckAllAsync();

        Assert.Equal(new[] { (ServiceState.Online, ServiceState.Offline), (ServiceState.Offline, ServiceState.Online) }, transitions);
        Assert.Equal(ServiceState.Online, monitor.GetResult(service.Id).State);
    }

    [Fact]
    public void Certificate_pins_detect_changes()
    {
        var store = new CertificatePinStore(() => PathFor("pins.json"));
        var uri = new Uri("https://pve.lan:8006/");

        Assert.Equal(CertificatePinMatch.NotPinned, store.Check(uri, "AA"));

        store.Pin(uri, "ab:cd:ef", "CN=pve", "CN=pve", DateTime.Now.AddYears(1));

        Assert.Equal(CertificatePinMatch.Matches, store.Check(new Uri("https://PVE.lan:8006/autre"), "ABCDEF"));
        Assert.Equal(CertificatePinMatch.Changed, store.Check(uri, "012345"));
        Assert.Equal(CertificatePinMatch.NotPinned, store.Check(new Uri("https://pve.lan:9443/"), "ABCDEF"));
        Assert.Equal("AB:CD:EF", CertificatePinStore.FormatFingerprint("abcdef"));

        var reloaded = new CertificatePinStore(() => PathFor("pins.json"));
        Assert.Equal(CertificatePinMatch.Matches, reloaded.Check(uri, "ABCDEF"));
        Assert.True(reloaded.Remove("pve.lan:8006"));
        Assert.Empty(reloaded.GetAll());
    }

    [Fact]
    public void Workspaces_keep_web_tabs_and_replace_by_name()
    {
        var store = new WorkspaceStore(() => PathFor("workspaces.json"));

        store.Save("Réseau", new[]
        {
            new WorkspaceTab { Url = "http://192.168.1.1/", Title = "Routeur" },
            new WorkspaceTab { Url = "about:blank", Title = "Vide" }
        });
        store.Save("réseau", new[] { new WorkspaceTab { Url = "https://pihole.lan/admin", Title = "Pi-hole", IsPinned = true } });

        var all = new WorkspaceStore(() => PathFor("workspaces.json")).GetAll();
        Assert.Single(all);
        Assert.Single(all[0].Tabs);
        Assert.True(all[0].Tabs[0].IsPinned);

        Assert.Throws<ArgumentException>(() => store.Save("Vide", new[] { new WorkspaceTab { Url = "about:blank" } }));
    }
}
