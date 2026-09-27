using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PommeBrowser.SupportServer.Tests;

public sealed class ReportEndpointTests
{
    const string Route = "/api/v1/support/reports";

    [Fact]
    public async Task Report_is_posted_to_discord_and_saved_with_its_log()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form(log: Encoding.UTF8.GetBytes("ligne de journal")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean());
        string id = body.GetProperty("reportId").GetString()!;
        Assert.Matches(@"^PB-\d{8}-[A-Z0-9]{6}$", id);

        (Uri url, string discordBody) = Assert.Single(server.Discord.Requests);
        Assert.Equal(SupportServerFactory.BugWebhook, url.ToString());
        Assert.Contains("Onglet figé", discordBody);
        Assert.Contains("Bug ou dysfonctionnement", discordBody);
        Assert.Contains(id, discordBody);
        Assert.Contains("ligne de journal", discordBody);
        Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", discordBody);

        string folder = Assert.Single(Directory.GetDirectories(Path.Combine(server.DataDirectory, "reports"), id, SearchOption.AllDirectories));
        Assert.Contains("Onglet figé", File.ReadAllText(Path.Combine(folder, "report.json")));
        Assert.Equal("ligne de journal", File.ReadAllText(Path.Combine(folder, "log.txt")));
    }

    [Fact]
    public async Task Category_without_its_own_webhook_uses_the_default_one()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form(category: "feature_request", module: "adblock"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        (Uri url, string discordBody) = Assert.Single(server.Discord.Requests);
        Assert.Equal(SupportServerFactory.DefaultWebhook, url.ToString());
        Assert.Contains("Bloqueur de publicités", discordBody);
    }

    [Fact]
    public async Task Unknown_category_is_filed_as_other()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        await client.PostAsync(Route, Reports.Form(category: "n'importe quoi"));

        (_, string discordBody) = Assert.Single(server.Discord.Requests);
        Assert.Contains("— Autre", discordBody);
    }

    [Theory]
    [InlineData("", "Une description")]
    [InlineData("Un titre", "   ")]
    public async Task Title_and_description_are_required(string title, string description)
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form(title: title, description: description));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("obligatoires", await response.Content.ReadAsStringAsync());
        Assert.Empty(server.Discord.Requests);
    }

    [Fact]
    public async Task Form_without_report_is_rejected()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        var form = new MultipartFormDataContent { { new StringContent("{ pas du json"), "report" } };
        HttpResponseMessage response = await client.PostAsync(Route, form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Plain_json_body_is_not_accepted()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_log_is_rejected()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form(log: new byte[ReportValidator.MaxLogBytes + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(server.Discord.Requests);
    }

    [Fact]
    public async Task Long_technical_information_is_attached_as_a_file()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        string technical = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"- Ligne technique {i}"));
        await client.PostAsync(Route, Reports.Form(technical: technical));

        (_, string discordBody) = Assert.Single(server.Discord.Requests);
        Assert.Contains("infos-techniques-PB-", discordBody);
        Assert.Contains("- Ligne technique 200", discordBody);
        Assert.Contains("version complète en pièce jointe", discordBody);
    }

    [Fact]
    public async Task Report_is_kept_on_disk_when_discord_fails()
    {
        using var server = new SupportServerFactory();
        server.Discord.Respond(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string id = body.GetProperty("reportId").GetString()!;
        Assert.Single(Directory.GetDirectories(Path.Combine(server.DataDirectory, "reports"), id, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Unavailable_when_discord_fails_and_nothing_is_stored()
    {
        using var server = new SupportServerFactory().With("SUPPORT_STORE_REPORTS", "false");
        server.Discord.Respond(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form());

        // 503 : le navigateur enregistre alors le rapport sur l'ordinateur de l'utilisateur.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Unavailable_when_nothing_is_configured()
    {
        using var server = new SupportServerFactory()
            .With("DISCORD_WEBHOOK_URL", null)
            .With("DISCORD_WEBHOOK_URL_BUG", null)
            .With("SUPPORT_STORE_REPORTS", "false");
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Discord_rate_limit_is_retried_once()
    {
        using var server = new SupportServerFactory();
        server.Discord.Respond(() =>
        {
            var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            tooMany.Headers.Add("X-RateLimit-Reset-After", "0.05");
            return tooMany;
        });
        using HttpClient client = server.CreateClient();

        HttpResponseMessage response = await client.PostAsync(Route, Reports.Form());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, server.Discord.Requests.Count);
    }

    [Fact]
    public async Task Too_many_reports_from_one_address_are_refused()
    {
        using var server = new SupportServerFactory().With("SUPPORT_REPORTS_PER_ADDRESS", "2");
        using HttpClient client = server.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(Route, Reports.Form())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(Route, Reports.Form())).StatusCode);
        HttpResponseMessage refused = await client.PostAsync(Route, Reports.Form());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Contains("Réessayez", await refused.Content.ReadAsStringAsync());
        Assert.Equal(2, server.Discord.Requests.Count);
    }

    [Theory]
    [InlineData("SUPPORT_PROXY_HOPS", "1", "X-Forwarded-For")]
    [InlineData("SUPPORT_BEHIND_CLOUDFLARE", "true", "CF-Connecting-IP")]
    public async Task Behind_a_proxy_each_client_address_has_its_own_limit(string setting, string value, string header)
    {
        using var server = new SupportServerFactory()
            .With("SUPPORT_REPORTS_PER_ADDRESS", "1")
            .With(setting, value);
        using HttpClient client = server.CreateClient();

        async Task<HttpStatusCode> SendFrom(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Reports.Form() };
            request.Headers.Add(header, address);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await SendFrom("203.0.113.10"));
        Assert.Equal(HttpStatusCode.Created, await SendFrom("203.0.113.20"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendFrom("203.0.113.10"));
    }

    [Fact]
    public async Task Health_check_is_not_rate_limited()
    {
        using var server = new SupportServerFactory().With("SUPPORT_REPORTS_PER_ADDRESS", "1");
        using HttpClient client = server.CreateClient();

        for (int i = 0; i < 10; i++)
            Assert.Equal("ok", await client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task Messages_follow_the_browser_language()
    {
        using var server = new SupportServerFactory();
        using HttpClient client = server.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Reports.Form(title: "") };
        request.Headers.AcceptLanguage.ParseAdd("en");
        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Contains("required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Invalid_webhook_address_stops_the_server()
    {
        using var server = new SupportServerFactory().With("DISCORD_WEBHOOK_URL", "https://exemple.fr/pas-un-webhook");

        var error = Assert.Throws<InvalidOperationException>(() => server.CreateClient());
        Assert.Contains("DISCORD_WEBHOOK_URL", error.Message);
    }

    [Fact]
    public void Control_characters_are_removed_and_fields_truncated()
    {
        var incoming = new IncomingReport
        {
            ClientReportId = "PB-20260927-AB12",
            ClientVersion = "0.9.9.0\" OR 1=1",
            Title = "Titre\u0007 sur\r\ndeux lignes" + new string('x', 400),
            Description = "Ligne 1\r\nLigne 2"
        };

        SupportReport report = ReportValidator.Validate(incoming, null, DateTimeOffset.UtcNow, "fr");

        Assert.StartsWith("Titre sur deux lignes", report.Title);
        Assert.Equal(ReportValidator.MaxTitle, report.Title.Length);
        Assert.Equal("Ligne 1\nLigne 2", report.Description);
        Assert.Equal("inconnue", report.ClientVersion);
        Assert.Equal("PB-20260927-AB12", report.ClientReportId);
    }

    [Fact]
    public async Task Old_reports_are_deleted_after_the_retention_period()
    {
        using var server = new SupportServerFactory().With("SUPPORT_RETENTION_DAYS", "30");
        using HttpClient client = server.CreateClient();
        await client.PostAsync(Route, Reports.Form());

        string reports = Path.Combine(server.DataDirectory, "reports");
        string saved = Assert.Single(Directory.GetDirectories(Directory.GetDirectories(reports)[0]));
        string old = Path.Combine(reports, "2020-01", "PB-20200101-OLD123");
        Directory.CreateDirectory(old);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));

        var store = (ReportStore)server.Services.GetService(typeof(ReportStore))!;
        Assert.Equal(1, store.DeleteExpired());

        Assert.False(Directory.Exists(Path.Combine(reports, "2020-01")));
        Assert.True(Directory.Exists(saved));
    }
}
