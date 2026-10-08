using System.Text;
using System.Text.Json;

namespace PommeFlash.Tests;

/// <summary>
/// Tenue dans le temps et robustesse : l'hôte ne garde rien d'un chargement à l'autre (mémoire,
/// objets de la page, flux), et des commandes malformées ne l'arrêtent pas.
/// </summary>
public sealed class EnduranceTests
{
    static readonly TimeSpan Scenario = TimeSpan.FromSeconds(90);

    static HostRun Start(TestServer server)
    {
        byte[] movie = new byte[20_000];
        new Random(17).NextBytes(movie);
        server.Add("movie.swf", movie, "application/x-shockwave-flash");
        server.Add("jeu/data.txt", Encoding.UTF8.GetBytes(new string('d', 5000)), "text/plain");
        return HostRun.Start(new[]
        {
            "--plugin", HostRun.HostVisiblePath(HostRun.PluginPath!),
            "--swf", server.Url("movie.swf"),
            "--page", server.Url("jeu/page.html"),
            "--hidden"
        }, code => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal) ? (true, "<number>5</number>") : (false, null));
    }

    static async Task<JsonElement> StatsAsync(HostRun host)
    {
        int before = host.Events.Count(e => e.GetProperty("event").GetString() == "stats");
        await host.SendAsync("stats");
        await host.WaitForAsync(h => h.Events.Count(e => e.GetProperty("event").GetString() == "stats") > before, TimeSpan.FromSeconds(20));
        return host.Events.Last(e => e.GetProperty("event").GetString() == "stats");
    }

    static async Task RoundAsync(HostRun host, int number, int loads)
    {
        string request = "boucle:" + loads;
        await host.SendAsync($"call {number} " + JsonSerializer.Serialize(new { request }));
        await host.WaitForAsync(h => h.Reports.Contains("cycle-done=" + number), TimeSpan.FromSeconds(120));
    }

    [Fact(Timeout = 300_000)]
    public async Task Repeated_loads_and_page_objects_leave_nothing_behind()
    {
        HostRun.SkipIfUnavailable();
        using var server = new TestServer();
        await using HostRun host = Start(server);
        await host.WaitForAsync(h => h.Reports.Contains("done"), Scenario);

        // Première série : ce qui se garde une fois pour toutes (identifiants, objets de la page…).
        await RoundAsync(host, 1, 100);
        JsonElement first = await StatsAsync(host);
        // Deuxième série identique : rien de plus ne doit rester.
        await RoundAsync(host, 2, 100);
        JsonElement second = await StatsAsync(host);

        Assert.Equal(0, second.GetProperty("streams").GetInt32());
        Assert.Equal(first.GetProperty("objects").GetInt64(), second.GetProperty("objects").GetInt64());
        long grown = second.GetProperty("memory").GetInt64() - first.GetProperty("memory").GetInt64();
        Assert.True(grown <= 2, $"Blocs de mémoire en plus après 100 chargements : {grown} ({first} → {second})");

        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact(Timeout = 180_000)]
    public async Task Malformed_commands_do_not_stop_the_host()
    {
        HostRun.SkipIfUnavailable();
        using var server = new TestServer();
        await using HostRun host = Start(server);
        await host.WaitForAsync(h => h.Reports.Contains("done"), Scenario);

        var random = new Random(23);
        var lines = new List<string>
        {
            "", "   ", "inconnue", "result", "result abc", "result 99999 {pas du json", "result 1 []", "result -5 {\"ok\":true}",
            "call", "call x y", "call 3", "call 4 {}", "call 5 {\"request\":123}", "call 6 {\"request\":null}", "call 7 [1,2]",
            "call 99999999999999999999 {\"request\":\"x\"}", "ping", "ping -1", "ping x", "ping 99999999999999999999",
            "stats now", "close please", "éè中😀", new string('x', 1_000_000),
            "call 8 " + JsonSerializer.Serialize(new { request = new string('<', 100_000) })
        };
        for (int i = 0; i < 200; i++)
        {
            char[] noise = new char[random.Next(1, 200)];
            for (int j = 0; j < noise.Length; j++)
                noise[j] = (char)random.Next(32, 0x2FF);
            lines.Add(new string(noise));
        }
        foreach (string line in lines)
            await host.SendAsync(line);

        // Toujours là : le fil du module répond, les appels aussi, et la fin reste normale.
        await host.SendAsync("ping 424242");
        await host.WaitForAsync(h => h.Events.Any(e => e.GetProperty("event").GetString() == "pong" && e.GetProperty("id").GetInt64() == 424242), TimeSpan.FromSeconds(30));
        string request = "<invoke name=\"encore\" returntype=\"javascript\"><arguments></arguments></invoke>";
        await host.SendAsync("call 77 " + JsonSerializer.Serialize(new { request }));
        await host.WaitForAsync(h => h.Events.Any(e => e.GetProperty("event").GetString() == "called" && e.GetProperty("id").GetInt32() == 77), TimeSpan.FromSeconds(30));
        Assert.Equal("retour:" + request, host.Events.Last(e => e.GetProperty("event").GetString() == "called" && e.GetProperty("id").GetInt32() == 77).GetProperty("value").GetString());

        await host.SendAsync("close");
        Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("destroy", host.Reports);
    }
}
