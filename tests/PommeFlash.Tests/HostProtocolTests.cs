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

    static bool IsLog(JsonElement e, string text)
        => e.GetProperty("event").GetString() == "log" &&
           e.TryGetProperty("message", out JsonElement message) && message.GetString()?.Contains(text, StringComparison.Ordinal) == true;

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
            "--height", "300"
        }.Concat(HostRun.IsWindowsHost ? new[] { "--hidden" } : Array.Empty<string>()), // Linux : fenêtre affichée, pour le clic et la touche simulés
        code => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal)
            ? (true, "<number>5</number>")
            : (false, null));

        await host.WaitForAsync(h => h.Reports.Contains("done"), Scenario);
        IReadOnlyList<string> reports = host.Reports;
        string page = server.Url("jeu/page.html");

        // Démarrage : table du navigateur, paramètres de l'élément, fenêtre.
        // Table des fonctions du navigateur : 58 pointeurs après l'en-tête (64 ou 32 bits).
        Assert.Contains($"init size={(HostRun.Is32BitHost ? 236 : 472)} version=29", reports);
        Assert.Contains("new mime=application/x-shockwave-flash mode=1 argc=12", reports);
        Assert.Contains("arg src=" + server.Url("movie.swf"), reports);
        Assert.Contains("arg flashvars=a=1&b=2", reports);
        Assert.Contains("arg wmode=window", reports);
        Assert.Contains("arg id=jeu", reports);
        Assert.Contains("window valid=1 width=400 height=300 type=1", reports);
        // Un clic dans le contenu lui donne le clavier (le clavier était ailleurs).
        Assert.Contains("click-focus=1", reports);
        if (!HostRun.IsWindowsHost)
        {
            // Linux : GTK 2 et XEmbed annoncés dès NP_Initialize, affichage X11 donné avec la fenêtre,
            // fenêtre du module branchée dans la prise ; après le clic, la touche tapée lui arrive
            // même sans rien de focalisable dedans et le pointeur ailleurs.
            Assert.Contains("toolkit=2 xembed=1", reports);
            Assert.Contains(reports, r => r.StartsWith("ws-info type=1 display=1 visual=1 depth=", StringComparison.Ordinal));
            Assert.Contains("plug embedded=1", reports);
            Assert.Contains("focus-away=1", reports);
            Assert.Contains("plug-key=97", reports);
        }
        JsonElement ready = host.Events.First(e => e.GetProperty("event").GetString() == "ready");
        Assert.NotEqual(0, ready.GetProperty("window").GetInt64());

        // Questions sur la page.
        // Identité de Basilisk, avec le système et l'architecture de l'hôte.
        string system = !HostRun.IsWindowsHost ? "X11; Linux x86_64" : HostRun.Is32BitHost ? "WOW64" : "Win64; x64";
        Assert.Contains(reports, r => r.StartsWith("ua=Mozilla/5.0", StringComparison.Ordinal) && r.Contains("Goanna/", StringComparison.Ordinal) &&
                                      r.Contains("Basilisk/", StringComparison.Ordinal) && r.Contains(system, StringComparison.Ordinal));
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
        Assert.Contains("notify url=detour.txt reason=1 data=4321", reports);
        Assert.Contains($"stream-done url={server.Url("jeu/echo")} bytes=5 hash={Fnv1a("hello"u8.ToArray()):x8} ordered=1 reason=0", reports);
        Assert.Contains("notify url=echo reason=0 data=9abc", reports);

        // Navigation et scripts transmis à PommeBrowser.
        Assert.Contains(host.Events, e => e.GetProperty("event").GetString() == "navigate" &&
                                          e.GetProperty("url").GetString() == "https://example.org/page" &&
                                          e.GetProperty("target").GetString() == "_blank");
        Assert.Contains(host.Events, e => e.GetProperty("event").GetString() == "script" &&
                                          e.GetProperty("code").GetString() == "window.alert('pomme')");

        // Sans partage avec la page : l'hôte a ses propres cookies, vides au départ.
        Assert.Contains("url-cookie=", reports);
        Assert.Contains("set-cookie=0", reports);
        Assert.DoesNotContain(host.Events, e => e.GetProperty("event").GetString() is "cookies" or "set-cookie");

        // Scripts de la page (ExternalInterface.call) : le module attend la réponse de PommeBrowser.
        Assert.Contains("script=<number>5</number>", reports);
        Assert.Contains("script-refused=failed", reports);
        Assert.Equal(2, host.Events.Count(e => e.GetProperty("event").GetString() == "eval"));

        // Fils et minuteries : tout revient sur le fil du module.
        Assert.Contains("async main=1 data=c0ffee", reports);
        Assert.Contains("timer ticks=3 main=1", reports);

        // Diagnostic : pages demandées avec une cible ; sous Windows, fenêtres ouvertes par le
        // module (comme une boîte de Flash), notées avec leur texte, et programmes Flash présents.
        Assert.Contains(host.Events, e => IsLog(e, "Page demandée par le contenu (cible _blank) : https://example.org/page"));
        if (HostRun.IsWindowsHost)
        {
            await host.WaitForAsync(h => h.Events.Any(e => IsLog(e, "Fenêtre ouverte par le module : « TEST dialogue »")), TimeSpan.FromSeconds(20));
            Assert.Contains(host.Events, e => IsLog(e, "« Texte du dialogue de test »"));
            // La liste des processus est lue (structure de la bonne taille, en 64 comme en 32 bits).
            string programs = host.Events.Where(e => IsLog(e, "Programmes Flash déjà en cours : ")).Select(e => e.GetProperty("message").GetString()!).Single();
            Assert.Matches(@"\(([1-9]\d*) processus vus\)$", programs);
        }

        // Appel de la page vers le contenu (ExternalInterface.addCallback) : CallFunction sur
        // l'objet scriptable du module, sur son fil, et sa réponse renvoyée à PommeBrowser.
        string request = "<invoke name=\"jeu\" returntype=\"javascript\"><arguments><string>été</string></arguments></invoke>";
        await host.SendAsync("call 7 " + JsonSerializer.Serialize(new { request }));
        await host.WaitForAsync(h => h.Events.Any(e => e.GetProperty("event").GetString() == "called"), TimeSpan.FromSeconds(20));
        JsonElement called = host.Events.First(e => e.GetProperty("event").GetString() == "called");
        Assert.Equal(7, called.GetProperty("id").GetInt32());
        Assert.True(called.GetProperty("ok").GetBoolean());
        Assert.Equal("retour:" + request, called.GetProperty("value").GetString());
        Assert.Contains("call main=1 request=" + request, host.Reports);

        // Méthodes de Flash appelées par la page (API JavaScript de Flash Player) : valeur en JSON.
        // Une méthode hors de cette liste, ou des arguments invalides, sont refusés sans appel.
        string Method(int id) => host.Events.Single(e => e.GetProperty("event").GetString() == "called" && e.GetProperty("id").GetInt32() == id)
            .GetProperty("value").GetString()!;
        await host.SendAsync("call 8 " + JsonSerializer.Serialize(new { request = """{"method":"PercentLoaded","args":[]}""" }));
        await host.SendAsync("call 9 " + JsonSerializer.Serialize(new { request = """{"method":"SetVariable","args":["/:etat","prêt \"ok\""]}""" }));
        await host.SendAsync("call 10 " + JsonSerializer.Serialize(new { request = """{"method":"GetVariable","args":["/:etat"]}""" }));
        await host.SendAsync("call 11 " + JsonSerializer.Serialize(new { request = """{"method":"CallFunction","args":["<invoke/>"]}""" }));
        await host.SendAsync("call 12 " + JsonSerializer.Serialize(new { request = """{"method":"GetVariable","args":[{"x":1}]}""" }));
        await host.WaitForAsync(h => h.Events.Count(e => e.GetProperty("event").GetString() == "called") >= 6, TimeSpan.FromSeconds(20));
        Assert.Equal("100", Method(8));
        Assert.Equal("null", Method(9));
        Assert.Equal("prêt \"ok\"", JsonSerializer.Deserialize<string>(Method(10)));
        Assert.Contains("method SetVariable /:etat=prêt \"ok\"", host.Reports);
        foreach (int refused in new[] { 11, 12 })
            Assert.False(host.Events.Single(e => e.GetProperty("event").GetString() == "called" && e.GetProperty("id").GetInt32() == refused).GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(host.Reports, r => r.StartsWith("call main=1 request=<invoke/>", StringComparison.Ordinal));

        // Fin demandée par PommeBrowser : instance détruite, sortie normale.
        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("destroy", host.Reports);
        Assert.Contains(host.Events, e => e.GetProperty("event").GetString() == "exit");
    }

    [Fact(Timeout = 180_000)]
    public async Task A_page_call_made_while_the_content_waits_for_a_script_is_answered_from_that_wait()
    {
        HostRun.SkipIfUnavailable();

        byte[] movie = new byte[20_000];
        new Random(5).NextBytes(movie);
        using var server = new TestServer();
        server.Add("movie.swf", movie, "application/x-shockwave-flash");
        server.Add("jeu/data.txt", "x"u8.ToArray(), "text/plain");
        const string request = "<invoke name=\"imbrique\" returntype=\"javascript\"><arguments></arguments></invoke>";

        // Comme dans un navigateur : le script demandé par le contenu (ExternalInterface.call)
        // appelle le contenu (fonction déclarée par addCallback) avant de rendre son résultat.
        static async Task<bool> Arrived(HostRun run, string kind)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!run.Events.Any(e => e.GetProperty("event").GetString() == kind))
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(15))
                    return false;
                await Task.Delay(20);
            }
            return true;
        }

        static async Task<(bool Ok, string? Value)> CallBackIntoTheContent(HostRun run)
        {
            // Battement de cœur : le contenu qui attend la page répond aussitôt (il n'est pas figé).
            await run.SendAsync("ping 42");
            if (!await Arrived(run, "pong"))
                return (false, null);
            await run.SendAsync("call 11 " + JsonSerializer.Serialize(new { request }));
            return await Arrived(run, "called") ? (true, "<number>5</number>") : (false, null);
        }

        await using HostRun host = HostRun.Start(new[]
        {
            "--plugin", HostRun.HostVisiblePath(HostRun.PluginPath!),
            "--swf", server.Url("movie.swf"),
            "--page", server.Url("jeu/page.html"),
            "--hidden"
        }, _ => (false, null), slowScripts: (run, code) => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal) ? CallBackIntoTheContent(run) : null);

        await host.WaitForAsync(h => h.Reports.Contains("done"), Scenario);
        IReadOnlyList<string> reports = host.Reports;

        // L'appel a été exécuté par le contenu pendant qu'il attendait le script, sur son fil,
        // puis le script a rendu son résultat.
        JsonElement called = host.Events.Single(e => e.GetProperty("event").GetString() == "called");
        Assert.Equal(11, called.GetProperty("id").GetInt32());
        Assert.True(called.GetProperty("ok").GetBoolean());
        Assert.Equal("retour:" + request, called.GetProperty("value").GetString());
        int call = reports.ToList().IndexOf("call main=1 request=" + request);
        int script = reports.ToList().IndexOf("script=<number>5</number>");
        Assert.True(call >= 0 && script > call, string.Join("\n", reports));
        Assert.Equal(42, host.Events.First(e => e.GetProperty("event").GetString() == "pong").GetProperty("id").GetInt64());

        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact(Timeout = 180_000)]
    public async Task Redirects_of_notified_loads_are_submitted_to_the_module()
    {
        HostRun.SkipIfUnavailable();

        byte[] movie = new byte[20_000];
        new Random(9).NextBytes(movie);
        byte[] data = Encoding.UTF8.GetBytes("données après redirection");
        using var server = new TestServer();
        server.Add("movie.swf", movie, "application/x-shockwave-flash");
        server.Redirect("jeu/data.txt", "/jeu/vrai.txt", "jeton=xyz; Path=/");
        server.Add("jeu/vrai.txt", data, "text/plain; charset=utf-8");
        // Le module refuse les redirections vers « interdit ».
        server.Redirect("jeu/detour.txt", "/jeu/interdit.txt", "autre=1; Path=/");
        server.Add("jeu/interdit.txt", data, "text/plain; charset=utf-8");

        await using HostRun host = HostRun.Start(new[]
        {
            "--plugin", HostRun.HostVisiblePath(HostRun.PluginPath!),
            "--swf", server.Url("movie.swf"),
            "--page", server.Url("jeu/page.html"),
            "--hidden"
        }, code => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal) ? (true, "<number>5</number>") : (false, null));

        await host.WaitForAsync(h => h.Reports.Contains("done") && h.Reports.Any(r => r.StartsWith("notify url=detour.txt", StringComparison.Ordinal)), Scenario);
        IReadOnlyList<string> reports = host.Reports;

        // Accordée : suivie, avec les cookies propres à l'hôte déposés au passage.
        Assert.Contains($"redirect url={server.Url("jeu/vrai.txt")} status=302 data=1234 main=1 allow=1", reports);
        Assert.Contains($"stream-done url={server.Url("jeu/vrai.txt")} bytes={data.Length} hash={Fnv1a(data):x8} ordered=1 reason=0", reports);
        Assert.Contains("notify url=data.txt reason=0 data=1234", reports);
        Assert.Contains("jeton=xyz", server.CookieHeader("jeu/vrai.txt")!.Split("; "));

        // Refusée : pas suivie, le chargement échoue.
        Assert.Contains($"redirect url={server.Url("jeu/interdit.txt")} status=302 data=4321 main=1 allow=0", reports);
        Assert.Contains("notify url=detour.txt reason=1 data=4321", reports);
        Assert.False(server.WasRequested("jeu/interdit.txt"));

        // Le contenu principal (sans notification) n'est pas soumis.
        Assert.DoesNotContain(reports, r => r.StartsWith("redirect url=" + server.Url("movie.swf"), StringComparison.Ordinal));

        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact(Timeout = 180_000)]
    public async Task Closing_during_a_download_ends_the_stream_then_the_instance()
    {
        HostRun.SkipIfUnavailable();

        byte[] movie = new byte[20_000];
        new Random(11).NextBytes(movie);
        using var server = new TestServer();
        server.Add("movie.swf", movie, "application/x-shockwave-flash");
        server.Add("jeu/data.txt", "x"u8.ToArray(), "text/plain");
        server.AddSlow("jeu/lent.bin");

        await using HostRun host = HostRun.Start(new[]
        {
            "--plugin", HostRun.HostVisiblePath(HostRun.PluginPath!),
            "--swf", server.Url("movie.swf"),
            "--page", server.Url("jeu/page.html"),
            "--hidden"
        }, code => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal) ? (true, "<number>5</number>") : (false, null));

        string slow = server.Url("jeu/lent.bin");
        await host.WaitForAsync(h => h.Reports.Contains("done") && h.Reports.Any(r => r.StartsWith("stream-open url=" + slow, StringComparison.Ordinal)), Scenario);

        // Battement de cœur : réponse du fil du module.
        await host.SendAsync("ping 7");
        await host.WaitForAsync(h => h.Events.Any(e => e.GetProperty("event").GetString() == "pong"), TimeSpan.FromSeconds(20));
        Assert.Equal(7, host.Events.First(e => e.GetProperty("event").GetString() == "pong").GetProperty("id").GetInt64());

        // Onglet fermé pendant le téléchargement : flux interrompu (NPRES_USER_BREAK) et notifié,
        // puis instance détruite, et fin normale sans attendre la fin du téléchargement.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        List<string> reports = host.Reports.ToList();
        int streamDone = reports.FindIndex(r => r.StartsWith("stream-done url=" + slow, StringComparison.Ordinal) && r.EndsWith(" reason=2", StringComparison.Ordinal));
        int notified = reports.IndexOf("notify url=lent.bin reason=2 data=7777");
        int destroyed = reports.IndexOf("destroy");
        Assert.True(streamDone >= 0 && notified > streamDone && destroyed > notified, string.Join("\n", reports));
    }

    [Fact(Timeout = 180_000)]
    public async Task With_shared_cookies_requests_carry_those_of_the_page()
    {
        HostRun.SkipIfUnavailable();

        byte[] movie = new byte[20_000];
        new Random(3).NextBytes(movie);
        byte[] data = Encoding.UTF8.GetBytes("données après redirection");
        using var server = new TestServer();
        server.Add("movie.swf", movie, "application/x-shockwave-flash");
        // data.txt redirige vers vrai.txt et dépose un cookie au passage.
        server.Redirect("jeu/data.txt", "/jeu/vrai.txt", "jeton=xyz; Path=/; HttpOnly");
        server.Add("jeu/vrai.txt", data, "text/plain; charset=utf-8");

        await using HostRun host = HostRun.Start(new[]
        {
            "--plugin", HostRun.HostVisiblePath(HostRun.PluginPath!),
            "--swf", server.Url("movie.swf"),
            "--page", server.Url("jeu/page.html"),
            "--share-cookies",
            "--hidden"
        }, code => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal) ? (true, "<number>5</number>") : (false, null),
        // PommeBrowser : cookies de la page, HttpOnly compris pour les chargements.
        (url, http) => http ? "session=abc; prefs=fr" : "prefs=fr");

        await host.WaitForAsync(h => h.Reports.Contains("done"), Scenario);
        IReadOnlyList<string> reports = host.Reports;
        IReadOnlyList<JsonElement> events = host.Events;

        // Chaque chargement, et chaque étape d'une redirection, porte les cookies de la page.
        Assert.Equal("session=abc; prefs=fr", server.CookieHeader("movie.swf"));
        Assert.Equal("session=abc; prefs=fr", server.CookieHeader("jeu/data.txt"));
        Assert.Equal("session=abc; prefs=fr", server.CookieHeader("jeu/vrai.txt"));
        Assert.Equal("session=abc; prefs=fr", server.CookieHeader("jeu/echo"));
        Assert.Contains(events, e => e.GetProperty("event").GetString() == "cookies" &&
                                     e.GetProperty("url").GetString() == server.Url("jeu/vrai.txt") && e.GetProperty("http").GetBoolean());

        // Le cookie déposé par la redirection est transmis à la page ; le contenu suit la redirection.
        Assert.Contains(events, e => e.GetProperty("event").GetString() == "set-cookie" &&
                                     e.GetProperty("url").GetString() == server.Url("jeu/data.txt") &&
                                     e.GetProperty("cookie").GetString() == "jeton=xyz; Path=/; HttpOnly" &&
                                     e.GetProperty("http").GetBoolean());
        Assert.Contains($"stream-done url={server.Url("jeu/vrai.txt")} bytes={data.Length} hash={Fnv1a(data):x8} ordered=1 reason=0", reports);
        Assert.Contains("notify url=data.txt reason=0 data=1234", reports);

        // NPN_GetValueForURL : les cookies qu'un script verrait ; NPN_SetValueForURL : cookie posé dans la page.
        Assert.Contains("url-cookie=prefs=fr", reports);
        Assert.Contains("set-cookie=0", reports);
        Assert.Contains(events, e => e.GetProperty("event").GetString() == "set-cookie" &&
                                     e.GetProperty("url").GetString() == server.Url("movie.swf") &&
                                     e.GetProperty("cookie").GetString() == "pose=1; Path=/" &&
                                     !e.GetProperty("http").GetBoolean());

        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
    }
}
