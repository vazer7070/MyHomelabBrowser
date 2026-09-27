using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace PommeBrowser.SupportServer.Tests;

/// <summary>
/// Serveur de support en mémoire, avec un faux Discord et un dossier de données temporaire.
/// </summary>
public sealed class SupportServerFactory : WebApplicationFactory<Program>
{
    public const string DefaultWebhook = "https://discord.com/api/webhooks/100/default-token";
    public const string BugWebhook = "https://discord.com/api/webhooks/200/bug-token";

    readonly Dictionary<string, string?> _settings = new()
    {
        ["DISCORD_WEBHOOK_URL"] = DefaultWebhook,
        ["DISCORD_WEBHOOK_URL_BUG"] = BugWebhook,
        ["SUPPORT_PROXY_HOPS"] = "0"
    };

    public FakeDiscord Discord { get; } = new();

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "pomme-support-server-" + Guid.NewGuid().ToString("N"));

    public SupportServerFactory With(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _settings.TryAdd("SUPPORT_DATA_DIR", DataDirectory);
        foreach ((string key, string? value) in _settings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
            services.AddHttpClient<DiscordNotifier>().ConfigurePrimaryHttpMessageHandler(() => Discord));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDirectory, recursive: true); } catch { }
    }
}

/// <summary>Enregistre les messages reçus et répond comme on le lui demande.</summary>
public sealed class FakeDiscord : HttpMessageHandler
{
    readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<(Uri Url, string Body)> Requests { get; } = new();

    public void Respond(params Func<HttpResponseMessage>[] responses)
    {
        foreach (var response in responses)
            _responses.Enqueue(response);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add((request.RequestUri!, body));

        return _responses.TryDequeue(out var next)
            ? next()
            : new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}

static class Reports
{
    public static MultipartFormDataContent Form(
        string title = "Onglet figé",
        string description = "L'onglet ne répond plus après la mise en veille.",
        string category = "bug",
        string module = "browser",
        byte[]? log = null,
        string technical = "- Système : Windows 11")
    {
        string json = $$"""
        {
          "clientReportId": "PB-20260927-AB12",
          "clientVersion": "0.9.9.0",
          "module": "{{module}}",
          "category": "{{category}}",
          "title": {{System.Text.Json.JsonSerializer.Serialize(title)}},
          "description": {{System.Text.Json.JsonSerializer.Serialize(description)}},
          "technicalInformation": {{System.Text.Json.JsonSerializer.Serialize(technical)}},
          "context": { "browserMode": "normal", "flashMode": "auto", "currentUrl": "https://exemple.fr/" }
        }
        """;

        var form = new MultipartFormDataContent
        {
            { new StringContent(json, Encoding.UTF8, "application/json"), "report" }
        };
        if (log != null)
            form.Add(new ByteArrayContent(log), "log", "pommebrowser-log.txt");
        return form;
    }
}
