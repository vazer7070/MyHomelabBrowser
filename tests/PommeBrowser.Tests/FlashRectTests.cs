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

    [Fact]
    public void The_tracker_script_posts_positions_on_the_ruffle_channel()
    {
        string script = RuffleContent.FlashTrackerScript("send(status)");

        Assert.Contains("send(status)", script);
        Assert.Contains("'rect:' + message", script);
        Assert.Contains("'rect:null'", script);
        Assert.DoesNotContain("__POST__", script);
        Assert.DoesNotContain("__RECT__", script);
        // Les fonctions déclarées par le contenu suivent l'élément remplacé par l'emplacement.
        Assert.Contains("window.__pommeFlashEquip(hole)", script);
    }

    [Fact]
    public void The_bridge_script_gives_call_function_to_the_element_of_the_content()
    {
        string script = RuffleContent.FlashBridgeScript("EmpireClient");

        Assert.Contains("chrome.webview.hostObjects.sync." + RuffleContent.FlashBridgeName, script);
        Assert.Contains("'CallFunction'", script);
        Assert.Contains("[data-pomme-flash]", script);
        Assert.Contains("const id = \"EmpireClient\";", script);
        Assert.DoesNotContain("__BRIDGE__", script);
        Assert.DoesNotContain("__ID__", script);
    }

    [Fact]
    public void The_identifier_of_the_element_is_a_safe_javascript_string()
    {
        string script = RuffleContent.FlashBridgeScript("a\"b</script>'c");

        Assert.DoesNotContain("a\"b", script);
        Assert.DoesNotContain("</script>", script);
        Assert.Contains("const id = \"\";", RuffleContent.FlashBridgeScript(null));
    }
}
