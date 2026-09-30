using System.Text;
using System.Text.Json;

namespace PommeFlash.Tests;

/// <summary>
/// L'hôte se comporte comme un navigateur vis-à-vis d'un module NPAPI : paramètres, fenêtre,
/// flux réseau au rythme du module, notifications, envois, objets de la page, adresse de la page
/// pour les règles de sécurité de Flash, fils, minuteries, navigation et fin propre.
/// </summary>
public sealed class HostProtocolTests
{
    static readonly TimeSpan Scenario = TimeSpan.FromSeconds(90);

    static uint Fnv1a(byte[] data)
    {
        uint hash = 2166136261u;
        foreach (byte b in data)
            hash = (hash ^ b) * 16777619u;
        return hash;
    }

    [Fact(Timeout = 180_000)]
    public async Task The_host_plays_the_role_of_the_browser()
    {
        HostRun.SkipIfUnavailable();

        byte[] movie = new byte[150_000];
        new Random(7).NextBytes(movie);
        byte[] data = Encoding.UTF8.GetBytes("données de test");
        using var server = new TestServer();
        server.Add("movie.swf", movie, "application/x-shockwave-flash");
        server.Add("jeu/data.txt", data, "text/plain; charset=utf-8");

        await using HostRun host = HostRun.Start(new[]
        {
            "--plugin", HostRun.HostVisiblePath(HostRun.PluginPath!),
            "--swf", server.Url("movie.swf"),
            "--page", server.Url("jeu/page.html"),
            "--flashvars", "a=1&b=2",
            "--id", "jeu",
            "--width", "400",
            "--height", "300",
            "--hidden"
        }, code => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal)
            ? (true, "<number>5</number>")
            : (false, null));

        await host.WaitForAsync(h => h.Reports.Contains("done"), Scenario);
        IReadOnlyList<string> reports = host.Reports;
        string page = server.Url("jeu/page.html");

        // Démarrage : table du navigateur, paramètres de l'élément, fenêtre.
        Assert.Contains("init size=472 version=29", reports);
        Assert.Contains("new mime=application/x-shockwave-flash mode=1 argc=12", reports);
        Assert.Contains("arg src=" + server.Url("movie.swf"), reports);
        Assert.Contains("arg flashvars=a=1&b=2", reports);
        Assert.Contains("arg wmode=window", reports);
        Assert.Contains("arg id=jeu", reports);
        Assert.Contains("window valid=1 width=400 height=300 type=1", reports);
        JsonElement ready = host.Events.First(e => e.GetProperty("event").GetString() == "ready");
        Assert.NotEqual(0, ready.GetProperty("window").GetInt64());

        // Questions sur la page.
        Assert.Contains(reports, r => r.StartsWith("ua=Mozilla/5.0", StringComparison.Ordinal) && r.Contains("PommeBrowser", StringComparison.Ordinal));
        Assert.Contains($"origin=http://127.0.0.1:{server.Port}", reports);
        Assert.Contains("javascript=1", reports);
        Assert.Contains("windowless=0", reports);
        Assert.Contains("evaluate=" + page + "__flashplugin_unique__", reports);
        Assert.Contains("href=" + page, reports);
        Assert.Contains("domain=127.0.0.1", reports);
        Assert.Contains("element-id=jeu", reports);

        // npruntime : identifiants et objets.
        Assert.Contains("ident same=1 name=abc", reports);
        Assert.Contains("ident-int string=0 value=42", reports);
        Assert.Contains("object refs=2 deallocated=1", reports);

        // Flux : le contenu entier, dans l'ordre, par bouchées de 1000 octets au plus.
        Assert.Contains($"stream-done url={server.Url("movie.swf")} bytes={movie.Length} hash={Fnv1a(movie):x8} ordered=1 reason=0", reports);
        Assert.Contains(reports, r => r.StartsWith($"stream-open url={server.Url("movie.swf")} mime=application/x-shockwave-flash end={movie.Length} notify=0 status=HTTP/1.1 200", StringComparison.Ordinal));

        // Adresses demandées : relatives à la page, notifiées, 404 signalé, envoi sans ses en-têtes.
        Assert.Contains($"stream-done url={server.Url("jeu/data.txt")} bytes={data.Length} hash={Fnv1a(data):x8} ordered=1 reason=0", reports);
        Assert.Contains("notify url=data.txt reason=0 data=1234", reports);
        Assert.Contains("notify url=missing.txt reason=1 data=5678", reports);
        Assert.Contains($"stream-done url={server.Url("jeu/echo")} bytes=5 hash={Fnv1a("hello"u8.ToArray()):x8} ordered=1 reason=0", reports);
        Assert.Contains("notify url=echo reason=0 data=9abc", reports);

        // Navigation et scripts transmis à PommeBrowser.
        Assert.Contains(host.Events, e => e.GetProperty("event").GetString() == "navigate" &&
                                          e.GetProperty("url").GetString() == "https://example.org/page" &&
                                          e.GetProperty("target").GetString() == "_blank");
        Assert.Contains(host.Events, e => e.GetProperty("event").GetString() == "script" &&
                                          e.GetProperty("code").GetString() == "window.alert('pomme')");

        // Scripts de la page (ExternalInterface.call) : le module attend la réponse de PommeBrowser.
        Assert.Contains("script=<number>5</number>", reports);
        Assert.Contains("script-refused=failed", reports);
        Assert.Equal(2, host.Events.Count(e => e.GetProperty("event").GetString() == "eval"));

        // Fils et minuteries : tout revient sur le fil du module.
        Assert.Contains("async main=1 data=c0ffee", reports);
        Assert.Contains("timer ticks=3 main=1", reports);

        // Fin demandée par PommeBrowser : instance détruite, sortie normale.
        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("destroy", host.Reports);
        Assert.Contains(host.Events, e => e.GetProperty("event").GetString() == "exit");
    }
}
