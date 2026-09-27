using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using PommeBrowser.SupportServer;

const long MaxRequestBytes = 2 * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

// Réglages lus une fois la configuration complète (variables d'environnement, tests).
builder.Services.AddSingleton(provider => SupportServerOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReportStore>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHttpClient<DiscordNotifier>(client =>
{
    // Le navigateur attend 35 s : deux essais et une courte attente doivent tenir dedans.
    client.Timeout = TimeSpan.FromSeconds(12);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PommeBrowser-SupportServer/1.0");
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = MaxRequestBytes;
});
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = MaxRequestBytes;
    form.ValueLengthLimit = 256 * 1024;
    form.ValueCountLimit = 16;
});

builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<SupportServerOptions>((forwarded, options) =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.ForwardLimit = Math.Max(options.ProxyHops, 1);
    // Le proxy est un autre conteneur ou une autre machine du réseau local.
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<SupportServerOptions>((limiter, options) =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context => IsReportRequest(context)
            ? RateLimitPartition.GetFixedWindowLimiter(ClientAddress(context, options), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.ReportsPerAddress,
                Window = options.AddressWindow
            })
            : RateLimitPartition.GetNoLimiter(string.Empty)),
        // Protège aussi le salon Discord si les demandes viennent de nombreuses adresses.
        PartitionedRateLimiter.Create<HttpContext, string>(context => IsReportRequest(context)
            ? RateLimitPartition.GetFixedWindowLimiter("tous", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.ReportsPerHour,
                Window = TimeSpan.FromHours(1)
            })
            : RateLimitPartition.GetNoLimiter(string.Empty)));

    limiter.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

        string language = Text.LanguageOf(context.HttpContext.Request);
        await context.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(Text.Get(language,
            "Trop de rapports envoyés. Réessayez dans quelques minutes.",
            "Too many reports sent. Please try again in a few minutes.")), cancellationToken);
    };
});

var app = builder.Build();
SupportServerOptions serverOptions = app.Services.GetRequiredService<SupportServerOptions>();

if (serverOptions.ProxyHops > 0)
    app.UseForwardedHeaders();

app.UseRateLimiter();

app.MapGet("/health", () => Results.Text("ok"));
app.MapPost(ReportRoute, ReceiveReportAsync);

app.Logger.LogInformation(
    "Serveur de support prêt. Discord : {Discord}. Copie sur disque : {Storage}.",
    serverOptions.DeliversToDiscord
        ? string.Join(", ", ReportCatalog.Categories.Where(c => serverOptions.WebhookFor(c) != null))
        : "non configuré",
    serverOptions.DataDirectory ?? "désactivée");

if (!serverOptions.DeliversToDiscord && serverOptions.DataDirectory == null)
    app.Logger.LogWarning("Ni webhook Discord ni copie sur disque : les rapports seront refusés (HTTP 503).");

app.Run();

static async Task<IResult> ReceiveReportAsync(
    HttpContext http,
    ReportStore store,
    DiscordNotifier discord,
    SupportServerOptions options,
    TimeProvider time,
    ILogger<ReportStore> logger)
{
    CancellationToken cancellationToken = http.RequestAborted;
    string language = Text.LanguageOf(http.Request);

    if (!options.DeliversToDiscord && !store.Enabled)
        return Error(HttpStatusCode.ServiceUnavailable, Text.Get(language,
            "Le service de support n'est pas encore configuré.",
            "The support service is not configured yet."));

    if (!http.Request.HasFormContentType)
        return Error(HttpStatusCode.UnsupportedMediaType, Text.Get(language,
            "Format de rapport non pris en charge.",
            "Unsupported report format."));

    IFormCollection form;
    try
    {
        form = await http.Request.ReadFormAsync(cancellationToken);
    }
    catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
    {
        return Error(HttpStatusCode.RequestEntityTooLarge, Text.Get(language,
            "Le rapport est trop volumineux.",
            "The report is too large."));
    }

    IncomingReport? incoming = null;
    if (form.TryGetValue("report", out var json))
    {
        try
        {
            incoming = JsonSerializer.Deserialize<IncomingReport>(json.ToString());
        }
        catch (JsonException)
        {
        }
    }

    byte[]? log = null;
    if (form.Files.GetFile("log") is { Length: > 0 } file)
    {
        if (file.Length > ReportValidator.MaxLogBytes)
            return Error(HttpStatusCode.RequestEntityTooLarge, Text.Get(language,
                "Le journal joint est trop volumineux.",
                "The attached log is too large."));

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        log = buffer.ToArray();
    }

    SupportReport report;
    try
    {
        report = ReportValidator.Validate(incoming, log, time.GetUtcNow(), language);
    }
    catch (ReportRejectedException ex)
    {
        return Error(HttpStatusCode.BadRequest, ex.Message);
    }

    bool stored = await store.TrySaveAsync(report, cancellationToken);
    bool delivered = await discord.TrySendAsync(report, cancellationToken);

    logger.LogInformation(
        "Rapport {ReportId} ({Module}/{Category}, version {Version}) : Discord {Delivered}, disque {Stored}",
        report.Id, report.Module, report.Category, report.ClientVersion, delivered ? "oui" : "non", stored ? "oui" : "non");

    // Ni Discord ni disque : le navigateur garde le rapport en local.
    if (!stored && !delivered)
        return Error(HttpStatusCode.ServiceUnavailable, Text.Get(language,
            "Le service de support ne peut pas transmettre le rapport pour le moment.",
            "The support service cannot forward the report right now."));

    return Results.Json(
        new AcceptedResponse(true, report.Id, Text.Get(language, "Rapport reçu. Merci !", "Report received. Thank you!")),
        statusCode: delivered ? StatusCodes.Status201Created : StatusCodes.Status202Accepted);
}

static IResult Error(HttpStatusCode status, string message)
    => Results.Json(new ErrorResponse(message), statusCode: (int)status);

static bool IsReportRequest(HttpContext context)
    => HttpMethods.IsPost(context.Request.Method) &&
       context.Request.Path.Equals(ReportRoute, StringComparison.OrdinalIgnoreCase);

static string ClientAddress(HttpContext context, SupportServerOptions options)
{
    if (options.BehindCloudflare &&
        IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString(), out IPAddress? cloudflareClient))
        return cloudflareClient.ToString();

    return context.Connection.RemoteIpAddress?.ToString() ?? "inconnue";
}

public partial class Program
{
    public const string ReportRoute = "/api/v1/support/reports";
}

record AcceptedResponse(bool Accepted, string ReportId, string Message);

record ErrorResponse(string Message);
