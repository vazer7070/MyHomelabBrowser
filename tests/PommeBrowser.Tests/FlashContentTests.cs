using PommeBrowser.Engine;

namespace PommeBrowser.Tests;

/// <summary>Description du contenu Flash envoyée par la page : données non fiables, bornées.</summary>
public sealed class FlashContentTests
{
    [Fact]
    public void A_complete_description_is_read()
    {
        FlashContent? content = FlashContent.Parse("""
            {"swf":"https://jeu.exemple.com/jeu.swf","page":"https://jeu.exemple.com/jouer",
             "flashvars":"niveau=3&son=1","width":800,"height":600,"id":"jeu",
             "params":{"quality":"high","WMODE":"opaque","allowscriptaccess":"sameDomain"}}
            """);

        Assert.NotNull(content);
        Assert.Equal("https://jeu.exemple.com/jeu.swf", content.Swf.AbsoluteUri);
        Assert.Equal("https://jeu.exemple.com/jouer", content.Page.AbsoluteUri);
        Assert.Equal("niveau=3&son=1", content.FlashVars);
        Assert.Equal((800, 600), (content.Width, content.Height));
        Assert.Equal("jeu", content.Id);
        Assert.Contains(new KeyValuePair<string, string>("wmode", "opaque"), content.Params);
        Assert.Equal(3, content.Params.Count);
    }

    [Theory]
    [InlineData("""{"swf":"file:///C:/secret.swf","page":"https://a.fr/"}""")]
    [InlineData("""{"swf":"javascript:alert(1)","page":"https://a.fr/"}""")]
    [InlineData("""{"swf":"https://a.fr/a.swf","page":"about:blank"}""")]
    [InlineData("""{"swf":"jeu.swf","page":"https://a.fr/"}""")]
    [InlineData("""{"page":"https://a.fr/"}""")]
    [InlineData("""["https://a.fr/a.swf"]""")]
    [InlineData("pas du json")]
    public void Only_web_contents_on_web_pages_are_accepted(string json)
    {
        Assert.Null(FlashContent.Parse(json));
    }

    [Fact]
    public void Implausible_sizes_and_oversized_values_are_dropped()
    {
        string flashVars = new('a', 20_000);
        string json = "{\"swf\":\"https://a.fr/a.swf\",\"page\":\"https://a.fr/\",\"width\":0,\"height\":99999," +
                      "\"flashvars\":\"" + flashVars + "\",\"id\":\"" + new string('i', 200) + "\"," +
                      "\"params\":{\"--plugin\":\"C:/autre.dll\",\"bon-nom\":\"ok\",\"long\":\"" + new string('v', 3000) + "\",\"nombre\":3}}";
        FlashContent? content = FlashContent.Parse(json);

        Assert.NotNull(content);
        Assert.Equal((800, 600), (content.Width, content.Height));
        Assert.Null(content.FlashVars);
        Assert.Null(content.Id);
        Assert.Equal(new[] { new KeyValuePair<string, string>("bon-nom", "ok") }, content.Params);
    }

    static FlashContent Sized(int width, int height, string page = "https://jeu.exemple.com/jouer")
        => new(new Uri("https://jeu.exemple.com/jeu.swf"), new Uri(page), null, width, height, null, Array.Empty<KeyValuePair<string, string>>());

    [Fact]
    public void The_largest_content_is_preferred_unless_it_has_an_advertising_size()
    {
        // Le plus grand, d'ordinaire.
        Assert.True(Sized(800, 600).IsPreferredOver(Sized(640, 480)));
        Assert.False(Sized(640, 480).IsPreferredOver(Sized(800, 600)));
        Assert.True(Sized(640, 480).IsPreferredOver(null));

        // Page « jeu + publicités » : une bannière plus grande que le jeu ne passe pas devant.
        Assert.True(Sized(160, 600).HasAdSize);
        Assert.True(Sized(300, 600).HasAdSize);
        Assert.False(Sized(400, 300).HasAdSize);
        Assert.True(Sized(400, 300).IsPreferredOver(Sized(300, 600)));
        Assert.False(Sized(300, 600).IsPreferredOver(Sized(400, 300)));
        // Entre deux publicités, la plus grande.
        Assert.True(Sized(300, 600).IsPreferredOver(Sized(728, 90)));
    }

    [Fact]
    public void The_detection_script_skips_tiny_contents_and_knows_the_advertising_sizes()
    {
        string script = RuffleContent.ProbeScript("https://pomme.invalid/", "post");

        Assert.Contains("if (width < 16 || height < 16) continue;", script);
        Assert.Contains("\"728x90\"", script);
        Assert.Contains("\"160x600\"", script);
        Assert.DoesNotContain("__AD_SIZES__", script);
    }
}
