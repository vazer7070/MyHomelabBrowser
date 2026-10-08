using System.Collections.Concurrent;
using System.Net.Sockets;
using PommeBrowser.Core;

namespace PommeBrowser.Tests;

/// <summary>
/// Instance unique : un second lancement transmet ses adresses à l'instance ouverte et s'arrête,
/// comme Firefox et Chrome quand un lien est ouvert depuis une autre application.
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    readonly List<string> _names = new();

    string UniqueName()
    {
        string name = "pommebrowser-test-" + Guid.NewGuid().ToString("N")[..12];
        _names.Add(name);
        return name;
    }

    static string UnixRoot()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
    }

    /// <summary>Dossiers des canaux de test retirés (Linux, macOS).</summary>
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (string name in _names)
        {
            try
            {
                Directory.Delete(Path.Combine(UnixRoot(), name), recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Second lancement sur un autre fil (sous Windows, le mutex est réentrant pour un même fil).</summary>
    static SingleInstance? ClaimElsewhere(string name, IReadOnlyList<string> targets)
    {
        SingleInstance? result = null;
        var thread = new Thread(() => result = SingleInstance.Claim(name, targets));
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void A_second_launch_hands_its_addresses_to_the_open_instance_and_stops()
    {
        string name = UniqueName();
        using SingleInstance first = SingleInstance.Claim(name, Array.Empty<string>())!;
        Assert.NotNull(first);
        var received = new BlockingCollection<IReadOnlyList<string>>();
        first.Attach(received.Add);

        SingleInstance? second = ClaimElsewhere(name, new[] { "https://exemple.fr/été?q=1", "file:///tmp/page.html" });

        Assert.Null(second);
        Assert.True(received.TryTake(out IReadOnlyList<string>? targets, TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "https://exemple.fr/été?q=1", "file:///tmp/page.html" }, targets);
    }

    [Fact]
    public void Addresses_received_before_the_window_exists_are_kept_until_it_attaches()
    {
        string name = UniqueName();
        using SingleInstance first = SingleInstance.Claim(name, Array.Empty<string>())!;

        Assert.Null(ClaimElsewhere(name, new[] { "https://a.example/" }));
        Assert.Null(ClaimElsewhere(name, Array.Empty<string>()));

        var received = new List<IReadOnlyList<string>>();
        first.Attach(received.Add);
        Assert.Equal(2, received.Count);
        Assert.Equal(new[] { "https://a.example/" }, received[0]);
        Assert.Empty(received[1]);
    }

    [Fact]
    public void The_channel_left_by_a_crashed_instance_is_taken_over()
    {
        if (OperatingSystem.IsWindows())
            return; // Le mutex et le tube disparaissent avec le processus.

        string name = UniqueName();
        // Entrée restée sur le disque sans personne pour l'écouter, comme après un plantage
        // (.NET retire la socket d'une instance fermée normalement).
        using (SingleInstance crashed = SingleInstance.Claim(name, Array.Empty<string>())!)
        {
        }
        string socketPath = Path.Combine(UnixRoot(), name, "instance.sock");
        File.WriteAllText(socketPath, "reste d'une instance arrêtée");
        using (var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            Assert.Throws<SocketException>(() => probe.Connect(new UnixDomainSocketEndPoint(socketPath)));

        using SingleInstance owner = SingleInstance.Claim(name, Array.Empty<string>())!;
        Assert.NotNull(owner);
        var received = new BlockingCollection<IReadOnlyList<string>>();
        owner.Attach(received.Add);
        Assert.Null(ClaimElsewhere(name, new[] { "https://b.example/" }));
        Assert.True(received.TryTake(out IReadOnlyList<string>? targets, TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "https://b.example/" }, targets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("[]")]
    [InlineData("""{"open":"https://a.example/"}""")]
    [InlineData("""{"autre":[]}""")]
    public void Invalid_requests_are_ignored(string line)
        => Assert.Null(SingleInstance.ParseRequest(line));

    [Fact]
    public void Requests_are_bounded()
    {
        string many = SingleInstance.FormatRequest(Enumerable.Range(0, 500).Select(i => "https://a.example/" + i).ToList());
        Assert.Equal(64, SingleInstance.ParseRequest(many)!.Count);

        string huge = SingleInstance.FormatRequest(new[] { "https://a.example/" + new string('x', 300_000) });
        Assert.Null(SingleInstance.ParseRequest(huge));

        Assert.Equal(new[] { "https://ok.example/" },
            SingleInstance.ParseRequest("""{"open":["https://ok.example/",42,null,""]}"""));
    }

    [Fact]
    public void The_channel_depends_on_the_user_and_the_data_folder()
    {
        string a = SingleInstance.ChannelName("/données/a");
        Assert.Equal(a, SingleInstance.ChannelName("/données/a"));
        Assert.NotEqual(a, SingleInstance.ChannelName("/données/b"));
        Assert.StartsWith("pommebrowser-", a);
    }

    [Fact]
    public void Launch_arguments_become_addresses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pomme-launch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "page.html"), "<p>ok</p>");

            IReadOnlyList<string> targets = LaunchTargets.Resolve(
                new[] { "--wait-pid", "https://exemple.fr/", "page.html", "exemple.fr", "  ", "inexistant.html" }, directory);

            Assert.Equal(new[]
            {
                "https://exemple.fr/",
                new Uri(Path.Combine(directory, "page.html")).AbsoluteUri,
                "exemple.fr",
                "inexistant.html"
            }, targets);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
