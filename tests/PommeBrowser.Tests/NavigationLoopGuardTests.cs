using PommeBrowser.Core;

namespace PommeBrowser.Tests;

/// <summary>Page qui se recharge sans fin (koramgame.com, une douzaine de fois par seconde).</summary>
public sealed class NavigationLoopGuardTests
{
    static readonly Uri Home = new("http://www.koramgame.com/");

    [Fact]
    public void Eight_pages_opened_in_a_few_seconds_are_a_loop_whatever_the_subdomain()
    {
        var guard = new NavigationLoopGuard();
        for (int i = 0; i < NavigationLoopGuard.LoopCount - 1; i++)
        {
            guard.Started(Home);
            Assert.False(guard.Opened(i % 2 == 0 ? Home : new Uri("http://koramgame.com/"), i * 80));
        }
        Assert.True(guard.Opened(Home, 700));
    }

    [Fact]
    public void Pages_opened_at_a_human_pace_are_not_a_loop()
    {
        var guard = new NavigationLoopGuard();
        for (int i = 0; i < 30; i++)
            Assert.False(guard.Opened(new Uri("https://exemple.fr/page" + i), i * 1_000L));
    }

    [Fact]
    public void The_trail_keeps_the_last_steps_without_their_parameters_and_the_hosts_upgraded_to_https()
    {
        var guard = new NavigationLoopGuard();
        guard.Started(new Uri("http://www.koramgame.com/login?token=secret"));
        guard.Upgraded(new Uri("https://www.koramgame.com/login?token=secret"), 100);
        guard.Redirected(new Uri("http://www.koramgame.com/"));
        guard.Opened(Home, 200);

        Assert.Equal("→ http://www.koramgame.com/login?… ⇧ https://www.koramgame.com/login?… ↪ http://www.koramgame.com/ ✓ http://www.koramgame.com/", guard.Trail);
        Assert.DoesNotContain("secret", guard.Trail);
        Assert.Equal(new[] { "www.koramgame.com" }, guard.UpgradedHosts(300));
        // Passage en HTTPS trop ancien : pas de cette boucle.
        Assert.Empty(guard.UpgradedHosts(100 + NavigationLoopGuard.LoopPeriod + 1));

        // Trajet borné ; tout oublié après traitement.
        for (int i = 0; i < 40; i++)
            guard.Started(Home);
        Assert.Equal(12, guard.Trail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(part => part == "→"));
        guard.Reset();
        Assert.Equal(string.Empty, guard.Trail);
        Assert.Empty(guard.UpgradedHosts(300));
    }
}
