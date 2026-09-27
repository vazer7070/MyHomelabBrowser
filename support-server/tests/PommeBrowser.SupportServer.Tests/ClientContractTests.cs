using System.Text;
using MyHomelabBrowser.classes.Support;
using MyHomelabBrowser.classes.Support.Models;
using MyHomelabBrowser.classes.Support.Transports;

namespace PommeBrowser.SupportServer.Tests;

/// <summary>
/// Le code d'envoi du navigateur (SupportApiTransport) face au vrai serveur :
/// garantit que les deux côtés restent compatibles.
/// </summary>
[Collection(nameof(ClientContractTests))]
[CollectionDefinition(nameof(ClientContractTests), DisableParallelization = true)]
public sealed class ClientContractTests : IDisposable
{
    public ClientContractTests()
        => Environment.SetEnvironmentVariable("POMMEBROWSER_SUPPORT_API_URL", "http://localhost");

    public void Dispose()
        => Environment.SetEnvironmentVariable("POMMEBROWSER_SUPPORT_API_URL", null);

    static SupportReportRequest Report(string title = "Page blanche") => new()
    {
        ClientReportId = "PB-20260927-C0DE",
        ClientVersion = "0.9.9.0",
        Module = "browser",
        ModuleLabel = "PommeBrowser",
        Category = "ui_ux",
        CategoryLabel = "Interface ou ergonomie",
        Title = title,
        Description = "Certaines pages restent blanches.",
        TechnicalInformation = "- Système : Windows 11",
        Context = new SupportReportContext { BrowserMode = "normal", FlashMode = "auto" }
    };

    [Fact]
    public async Task Browser_transport_delivers_a_report_to_the_server()
    {
        using var server = new SupportServerFactory();
        using var transport = new SupportApiTransport(server.CreateClient());
        var log = new SupportAttachment
        {
            FileName = "pommebrowser-log-PB-20260927-C0DE.txt",
            ContentType = "text/plain; charset=utf-8",
            Content = Encoding.UTF8.GetBytes("journal du navigateur")
        };

        SupportSubmissionResult result = await transport.SendAsync(Report(), log);

        Assert.True(result.Success);
        Assert.Equal(SupportDeliveryChannel.BackendApi, result.Channel);
        Assert.Matches(@"^PB-\d{8}-[A-Z0-9]{6}$", result.ReportId);

        (_, string discordBody) = Assert.Single(server.Discord.Requests);
        Assert.Contains("Page blanche", discordBody);
        Assert.Contains("journal du navigateur", discordBody);
        Assert.Contains("Interface ou ergonomie", discordBody);
    }

    [Fact]
    public async Task Server_refusal_reaches_the_browser_with_its_message()
    {
        using var server = new SupportServerFactory();
        using var transport = new SupportApiTransport(server.CreateClient());

        var error = await Assert.ThrowsAsync<SupportApiRejectedException>(() => transport.SendAsync(Report(title: ""), null));

        Assert.Contains("obligatoires", error.Message);
    }

    [Fact]
    public async Task Unavailable_server_lets_the_browser_fall_back_to_a_local_file()
    {
        using var server = new SupportServerFactory().With("SUPPORT_STORE_REPORTS", "false");
        server.Discord.Respond(() => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway));
        using var transport = new SupportApiTransport(server.CreateClient());

        await Assert.ThrowsAsync<SupportApiUnavailableException>(() => transport.SendAsync(Report(), null));
    }
}
