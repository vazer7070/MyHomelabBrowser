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
    public void A_content_is_the_same_whatever_its_size_but_not_with_another_file_page_or_flashvars()
    {
        FlashContent logo = Sized(600, 248, "http://na62.evony.com/s2.html");

        Assert.True(logo.IsSameAs(Sized(760, 600, "http://na62.evony.com/s2.html#jeu")));
        Assert.False(logo.IsSameAs(Sized(600, 248, "http://na62.evony.com/s3.html")));
        Assert.False(logo.IsSameAs(logo with { Swf = new Uri("http://cdn.evony.com/client.swf") }));
        Assert.False(logo.IsSameAs(logo with { FlashVars = "server=na62" }));
    }

    [Fact]
    public void The_detection_script_skips_declared_tiny_contents_and_knows_the_advertising_sizes()
    {
        string script = RuffleContent.ProbeScript("https://pomme.invalid/", "post");

        // Seule une taille déclarée minuscule écarte un contenu : un contenu encore caché ou en
        // pourcentage (taille affichée nulle) reste candidat.
        Assert.Contains("const tiny = (declaredWidth !== null && declaredWidth < 16) || (declaredHeight !== null && declaredHeight < 16);", script);
        Assert.Contains("if (item.tiny) continue;", script);
        Assert.DoesNotContain("if (width < 16 || height < 16) continue;", script);
        // Tous les contenus du document sont aussi décrits (client d'un jeu caché ou minuscule).
        Assert.Contains("post('" + FlashContent.ListPrefix + "' + list)", script);
        Assert.DoesNotContain("__CONTENTS__", script);
        // Les éléments déjà remplacés par Ruffle comptent, et la description est refaite à chaque lecteur.
        Assert.Contains("'object, embed, ruffle-object, ruffle-embed'", script);
        Assert.Contains("report();", script);
        // Messages d'un cadre relayés par le document principal.
        Assert.Contains("window.top.postMessage({ __pommeRuffle: String(status) }, '*')", script);
        Assert.DoesNotContain("__RECT__", script);
        Assert.Contains("\"728x90\"", script);
        Assert.Contains("\"160x600\"", script);
        Assert.DoesNotContain("__AD_SIZES__", script);
    }

    [Fact]
    public void A_list_describes_every_content_of_one_document()
    {
        const string logo = """{"swf":"http://www.evony.com/Logo2.swf","page":"http://na62.evony.com/s2.html","width":600,"height":248,"id":"flashClient"}""";
        const string client = """{"swf":"http://cdn.evony.com/EvonyClient.swf","page":"http://na62.evony.com/s2.html","width":1,"height":1,"id":"client"}""";

        IReadOnlyList<FlashContent> list = FlashContent.ParseList("[" + logo + "," + client + "," + logo + ",{\"swf\":\"file:///c:/x.swf\"}]");

        // Les deux contenus, une fois chacun ; une description invalide est ignorée.
        Assert.Equal(new[] { "flashClient", "client" }, list.Select(c => c.Id));
        Assert.Equal(new Uri("http://cdn.evony.com/EvonyClient.swf"), list[1].Swf);
        // Contenus de documents différents : liste refusée ; pas un tableau : vide.
        Assert.Empty(FlashContent.ParseList("[" + logo + "," + logo.Replace("s2.html", "s3.html") + "]"));
        Assert.Empty(FlashContent.ParseList(logo));
        Assert.Empty(FlashContent.ParseList("pas du json"));
        // Bornée.
        string many = "[" + string.Join(",", Enumerable.Range(0, 40).Select(i => logo.Replace("Logo2", "Logo" + i))) + "]";
        Assert.Equal(FlashContent.MaxListed, FlashContent.ParseList(many).Count);
    }

    [Fact]
    public void Two_elements_of_the_same_file_with_different_identifiers_are_different_contents()
    {
        FlashContent first = Sized(600, 248) with { Id = "a" };

        Assert.True(first.IsSameAs(first with { Width = 800 }));
        Assert.False(first.IsSameAs(first with { Id = "b" }));
        Assert.False(first.IsSameAs(first with { Id = null }));
        Assert.Equal(first.Identity, (first with { Height = 10 }).Identity);
        Assert.NotEqual(first.Identity, (first with { Id = "b" }).Identity);
        Assert.Equal("https://jeu.exemple.com/jeu.swf (600×248, id a)", first.ToString());
    }

    [Fact]
    public void On_a_site_read_by_the_integrated_engine_contents_are_only_described_and_ruffle_can_be_stopped()
    {
        string script = RuffleContent.ProbeScript("https://pomme.invalid/", "post", new[] { "Game.FR.Demon.Koramgame.com" });

        // Sites retenus (hôte de la page principale, vu aussi depuis un cadre d'un autre site).
        Assert.Contains("const INTEGRATED = new Set([\"game.fr.demon.koramgame.com\"]);", script);
        Assert.Contains("location.ancestorOrigins", script);
        Assert.DoesNotContain("__INTEGRATED__", script);
        // Contenus décrits, Ruffle pas chargé.
        Assert.Contains("if (detectOnly || window.__pommeRuffleStopped) {", script);
        // Arrêt demandé par le document parent (ou lui-même), transmis aux cadres ; jamais par un cadre enfant.
        Assert.Contains("event.data.__pommeStopRuffle === true && (event.source === window.parent || event.source === window)", script);
        Assert.Contains("window[i].postMessage({ __pommeStopRuffle: true }, '*')", script);
        Assert.Contains("player.pause()", script);
        Assert.Contains("__pommeStopRuffle", RuffleContent.StopRuffleScript);
        // Sans site retenu : liste vide.
        Assert.Contains("const INTEGRATED = new Set([]);", RuffleContent.ProbeScript("https://pomme.invalid/", "post"));
    }

    [Fact]
    public void Messages_of_a_frame_reach_the_browser_through_the_main_document_after_an_origin_check()
    {
        string script = RuffleContent.ProbeScript("https://pomme.invalid/", "post");

        // Un cadre (jeu dans une iframe, Evony…) envoie ses messages au document principal, qui les
        // transmet : pas d'abonnement aux cadres dans le moteur (WebView2 s'arrêtait sur Google).
        Assert.Contains("window.top.postMessage({ __pommeRuffle: String(status) }, '*')", script);
        Assert.Contains("if (window === window.top) {", script);
        // Jamais de position de suivi depuis un cadre ; une description de contenu, seule ou en
        // liste, seulement de la page qu'elle décrit (même origine que le cadre qui l'envoie).
        Assert.Contains("if (status.startsWith('" + RuffleContent.RectPrefix + "')) return;", script);
        Assert.Contains("if (new URL(JSON.parse(status.slice('" + FlashContent.MessagePrefix + "'.length)).page).origin !== event.origin) return;", script);
        Assert.Contains("list.some(item => new URL(item.page).origin !== event.origin)", script);
    }
}
