using PommeBrowser.Engine;

namespace PommeBrowser.Tests;

/// <summary>
/// Position du contenu lu par le moteur intégré dans la page (script de suivi) : données non
/// fiables, bornées, et placement de la fenêtre du lecteur dans la zone de la page.
/// </summary>
public sealed class FlashRectTests
{
    [Fact]
    public void A_position_is_read()
    {
        FlashRect? rect = FlashRect.Parse("""{"x":10.5,"y":-20,"w":800,"h":600,"dpr":1.5,"visible":true}""");

        Assert.Equal(new FlashRect(10.5, -20, 800, 600, 1.5, true), rect);
    }

    [Theory]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600}""")]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":0,"visible":true}""")]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":64,"visible":true}""")]
    [InlineData("""{"x":0,"y":0,"w":-1,"h":600,"dpr":1,"visible":true}""")]
    [InlineData("""{"x":0,"y":0,"w":1000000,"h":600,"dpr":1,"visible":true}""")]
    [InlineData("""{"x":1e300,"y":0,"w":800,"h":600,"dpr":1,"visible":true}""")]
    [InlineData("""{"x":null,"y":0,"w":800,"h":600,"dpr":1,"visible":true}""")]
    [InlineData("""{"x":"0","y":0,"w":800,"h":600,"dpr":1,"visible":true}""")]
    [InlineData("""[0,0,800,600]""")]
    [InlineData("null")]
    [InlineData("pas du json")]
    public void Invalid_positions_are_rejected(string json)
    {
        Assert.Null(FlashRect.Parse(json));
    }

    [Theory]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":1,"visible":false}""")]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":1,"visible":"true"}""")]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":1}""")]
    [InlineData("""{"x":0,"y":0,"w":0,"h":600,"dpr":1,"visible":true}""")]
    public void Hidden_or_empty_contents_are_not_placed(string json)
    {
        FlashRect? rect = FlashRect.Parse(json);

        Assert.NotNull(rect);
        Assert.False(rect.Visible);
        Assert.Null(rect.Place(1000, 800, 1));
    }

    [Fact]
    public void A_fully_visible_content_fills_its_view()
    {
        var rect = new FlashRect(100, 50, 400, 300, 1, true);

        FlashPlacement? placement = rect.Place(1000, 800, 1);

        Assert.Equal(new FlashPlacement(100, 50, 400, 300, 0, 0, 400, 300), placement);
    }

    [Fact]
    public void A_content_scrolled_past_the_top_is_cut_and_shifted()
    {
        // 120 pixels CSS hors de la zone, en haut : la vue n'en montre que le bas.
        var rect = new FlashRect(100, -120, 400, 300, 1, true);

        FlashPlacement? placement = rect.Place(1000, 800, 1);

        Assert.Equal(new FlashPlacement(100, 0, 400, 180, 0, -120, 400, 300), placement);
    }

    [Fact]
    public void A_content_larger_than_the_page_area_is_cut_on_every_side()
    {
        var rect = new FlashRect(-50, -30, 1200, 900, 1, true);

        FlashPlacement? placement = rect.Place(1000, 800, 1);

        Assert.Equal(new FlashPlacement(0, 0, 1000, 800, -50, -30, 1200, 900), placement);
    }

    [Fact]
    public void A_content_out_of_the_page_area_is_not_placed()
    {
        Assert.Null(new FlashRect(100, 900, 400, 300, 1, true).Place(1000, 800, 1));
        Assert.Null(new FlashRect(100, -300, 400, 300, 1, true).Place(1000, 800, 1));
        Assert.Null(new FlashRect(1000, 0, 400, 300, 1, true).Place(1000, 800, 1));
        Assert.Null(new FlashRect(100, 50, 400, 300, 1, true).Place(0, 0, 1));
    }

    [Fact]
    public void Screen_scaling_and_page_zoom_are_applied()
    {
        // Écran à 150 % et page zoomée à 200 % : 1 pixel CSS = 3 pixels de l'écran = 2 DIP.
        var rect = new FlashRect(10, 20, 100, 50, 3, true);

        FlashPlacement? placement = rect.Place(1000, 800, 1.5);

        Assert.Equal(new FlashPlacement(20, 40, 200, 100, 0, 0, 300, 150), placement);
    }

    [Fact]
    public void The_player_window_is_placed_from_the_truncated_view_origin()
    {
        // Vue à 10,4 DIP × 1,25 = 13 pixels ; contenu à 13,33 pixels arrondis à 13.
        var rect = new FlashRect(10.4, 0, 100, 100, 1.25, true);

        FlashPlacement? placement = rect.Place(1000, 800, 1.25);

        Assert.NotNull(placement);
        Assert.Equal(0, placement.Value.ClientX);
        Assert.Equal((125, 125), (placement.Value.ClientWidth, placement.Value.ClientHeight));
    }

    static FlashContent Content(string swf = "http://www.evony.com/Logo2.swf", string page = "http://na62.evony.com/s2.html", string? id = "flashClient")
        => new(new Uri(swf), new Uri(page), null, 600, 248, id, Array.Empty<KeyValuePair<string, string>>());

    [Fact]
    public void The_tracker_script_posts_positions_on_the_ruffle_channel()
    {
        string script = RuffleContent.FlashTrackerScript("send(status)", "f1", Content());

        Assert.Contains("send(status)", script);
        // Messages de l'emplacement : « rect:f1:… ».
        Assert.Contains("const slot = \"f1\";", script);
        Assert.Contains("post('rect:' + slot + ':' + payload)", script);
        Assert.Contains("say('null')", script);
        Assert.DoesNotContain("__POST__", script);
        Assert.DoesNotContain("__RECT__", script);
        // Élément retrouvé par son fichier et son identifiant.
        Assert.Contains("const wanted = { swf: \"http://www.evony.com/Logo2.swf\", id: \"flashClient\" };", script);
        Assert.DoesNotContain("__WANTED__", script);
        Assert.DoesNotContain("__FIND__", script);
        // Les fonctions du contenu suivent l'élément remplacé par l'emplacement.
        Assert.Contains("root.__pommeFlashEquips && root.__pommeFlashEquips[slot]", script);
        // Contenu retiré par la page : signalé, le lecteur s'arrête.
        Assert.Contains("say('gone')", script);
        // Document principal : pas de recherche de cadre.
        Assert.Contains("const root = window;", script);
        Assert.DoesNotContain("__ROOT__", script);
        Assert.DoesNotContain("__SLOT__", script);
        // Contenu sans identifiant : seul le fichier compte.
        Assert.Contains("id: null }", RuffleContent.FlashTrackerScript("send(status)", "f2", Content(id: null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("f1'")]
    [InlineData("f1:x")]
    [InlineData("abcdefghijklmnopq")]
    public void Slot_keys_are_short_letters_and_digits(string slot)
    {
        Assert.Throws<ArgumentException>(() => RuffleContent.FlashTrackerScript("send(status)", slot, Content()));
        Assert.Throws<ArgumentException>(() => RuffleContent.FlashBridgeScript(slot, Content(), RuffleContent.NewFlashBridgeToken()));
    }

    [Fact]
    public void The_tracker_script_finds_a_content_in_a_frame_of_the_page()
    {
        string script = RuffleContent.FlashTrackerScript("send(status)", "f1", Content(), new Uri("http://na62.evony.com/s2.html?a='b'"));

        Assert.DoesNotContain("const root = window;", script);
        Assert.Contains("frameElement", script);
        // Adresse du cadre : chaîne JavaScript sûre.
        Assert.Contains("(\"http://na62.evony.com/s2.html?a=\\u0027b\\u0027\")", script);
        Assert.DoesNotContain("__PAGE__", script);
        Assert.DoesNotContain("__ROOT__", script);
    }

    [Fact]
    public void A_script_runs_in_the_window_of_its_frame()
    {
        string script = RuffleContent.InWindowOf(new Uri("http://na62.evony.com/s2.html"), "alert(\"</script>\")");

        // Script passé en chaîne JSON, exécuté par l'eval de la fenêtre du cadre (portée globale du cadre).
        Assert.Contains("w.eval(\"alert(\\u0022\\u003C/script\\u003E\\u0022)\")", script);
        Assert.Contains("\"http://na62.evony.com/s2.html\"", script);
        Assert.DoesNotContain("__SCRIPT__", script);
        Assert.DoesNotContain("__WINDOW__", script);
    }

    [Fact]
    public void A_content_of_a_frame_is_cut_to_the_area_of_its_frame()
    {
        FlashRect? rect = FlashRect.Parse("""{"x":95,"y":75,"w":600,"h":248,"dpr":1,"visible":true,"clip":{"x":105,"y":55,"w":600,"h":200}}""");

        Assert.NotNull(rect);
        Assert.Equal(new FlashClip(105, 55, 600, 200), rect.Clip);
        FlashPlacement? placement = rect.Place(1000, 800, 1);
        // Partie dans le cadre : de 105 à 695 en largeur, de 75 à 255 en hauteur ; lecteur décalé de 10 pixels.
        Assert.Equal(new FlashPlacement(105, 75, 590, 180, -10, 0, 600, 248), placement);
    }

    [Fact]
    public void A_content_scrolled_out_of_its_frame_is_not_placed()
    {
        var rect = new FlashRect(100, 400, 400, 300, 1, true, new FlashClip(100, 50, 400, 300));

        Assert.Null(rect.Place(1000, 800, 1));
    }

    [Theory]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":1,"visible":true,"clip":{"x":0,"y":0,"w":-5,"h":10}}""")]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":1,"visible":true,"clip":{"x":1e300,"y":0,"w":5,"h":10}}""")]
    [InlineData("""{"x":0,"y":0,"w":800,"h":600,"dpr":1,"visible":true,"clip":{"x":0,"y":0,"w":5}}""")]
    public void Invalid_frame_areas_are_rejected(string json)
    {
        Assert.Null(FlashRect.Parse(json));
    }

    [Fact]
    public void A_frame_area_that_is_not_an_object_is_ignored()
    {
        FlashRect? rect = FlashRect.Parse("""{"x":0,"y":0,"w":800,"h":600,"dpr":1,"visible":true,"clip":null}""");

        Assert.NotNull(rect);
        Assert.Null(rect.Clip);
    }

    [Fact]
    public void The_bridge_script_gives_call_function_and_flash_methods_to_the_element_of_the_content()
    {
        string token = RuffleContent.NewFlashBridgeToken();
        string script = RuffleContent.FlashBridgeScript("f2", Content(id: "EmpireClient"), token);

        Assert.Contains("chrome.webview.hostObjects.sync." + RuffleContent.FlashBridgeName, script);
        Assert.Contains("'CallFunction'", script);
        // Chaque requête porte la clé de l'emplacement : « f2|requête ».
        Assert.Contains("const slot = \"f2\";", script);
        Assert.Contains("const message = slot + '|' + String(request);", script);
        Assert.Contains("id: \"EmpireClient\" }", script);
        // Méthodes de Flash (PercentLoaded…), en JSON.
        Assert.Contains("\"PercentLoaded\"", script);
        Assert.Contains("JSON.stringify({ method: name, args })", script);
        Assert.Contains("[data-pomme-flash-hole=\"' + slot + '\"]", script);
        foreach (string placeholder in new[] { "__BRIDGE__", "__SLOT__", "__WANTED__", "__FIND__", "__METHODS__" })
            Assert.DoesNotContain(placeholder, script);
        // Sous WebKitGTK, chaque requête au schéma du pont porte le jeton du pont.
        Assert.Contains("'" + RuffleContent.FlashBridgeScheme + "://call/?t=" + token + "&r=' + encodeURIComponent(message)", script);
        Assert.DoesNotContain("__TOKEN__", script);
    }

    [Fact]
    public void Bridge_tokens_are_random_hexadecimal_strings()
    {
        string first = RuffleContent.NewFlashBridgeToken();
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, RuffleContent.NewFlashBridgeToken());
        // Un jeton qui ne serait pas hexadécimal n'entre jamais dans le script.
        Assert.ThrowsAny<FormatException>(() => RuffleContent.FlashBridgeScript("f1", Content(), "x'+alert(1)+'"));
    }

    [Fact]
    public void A_bridge_request_is_accepted_only_with_the_token_of_the_player()
    {
        string token = RuffleContent.NewFlashBridgeToken();
        string request = "<invoke name=\"jeu\" returntype=\"javascript\"><arguments><string>été &amp; co</string></arguments></invoke>";
        string url = RuffleContent.FlashBridgeScheme + "://call/?t=" + token + "&r=" + Uri.EscapeDataString(request);

        Assert.Equal(request, RuffleContent.ParseFlashBridgeRequest(url, token));
        // Autre jeton (cadre d'un autre site), sans jeton, jeton tronqué, ancienne forme, autre schéma.
        Assert.Null(RuffleContent.ParseFlashBridgeRequest(url, RuffleContent.NewFlashBridgeToken()));
        Assert.Null(RuffleContent.ParseFlashBridgeRequest(url, null));
        Assert.Null(RuffleContent.ParseFlashBridgeRequest(RuffleContent.FlashBridgeScheme + "://call/?t=" + token[..8] + "&r=x", token));
        Assert.Null(RuffleContent.ParseFlashBridgeRequest(RuffleContent.FlashBridgeScheme + "://call/?r=" + Uri.EscapeDataString(request), token));
        Assert.Null(RuffleContent.ParseFlashBridgeRequest("https://call/?t=" + token + "&r=x", token));
        // Trop longue.
        string huge = new('a', RuffleContent.MaxFlashCallLength + 1);
        Assert.Null(RuffleContent.ParseFlashBridgeRequest(RuffleContent.FlashBridgeScheme + "://call/?t=" + token + "&r=" + huge, token));
    }

    [Fact]
    public void The_identifier_and_the_file_of_the_element_are_safe_javascript_strings()
    {
        string script = RuffleContent.FlashBridgeScript("f1", Content(swf: "http://a.fr/x.swf?q=</script>", id: "a\"b</script>'c"), RuffleContent.NewFlashBridgeToken());

        Assert.DoesNotContain("a\"b", script);
        Assert.DoesNotContain("</script>", script);
    }
}
