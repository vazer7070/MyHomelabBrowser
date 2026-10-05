using System.Text.Json;
using PommeBrowser.Engine;

namespace PommeBrowser.Tests;

/// <summary>
/// Scripts d'un contenu Flash dans un cadre d'une autre origine que la page (Demon Slayer : jeu de
/// s81fr.sq.koramgame.com dans game.fr.demon.koramgame.com) : exécutés dans ce cadre seulement.
/// </summary>
public sealed class FlashFramesTests
{
    static readonly Uri Game = new("http://s81fr.sq.koramgame.com/client/game.jsp?user=1&key=abc");

    const string Tree = """
        { "frameTree": {
            "frame": { "id": "MAIN", "url": "http://game.fr.demon.koramgame.com/?sid=s158" },
            "childFrames": [
              { "frame": { "id": "PUB", "url": "https://pub.exemple.com/banniere.html" } },
              { "frame": { "id": "JEU", "url": "http://s81fr.sq.koramgame.com/client/game.jsp?user=1&key=abc" },
                "childFrames": [ { "frame": { "id": "VIDE", "url": "about:blank" } } ] }
            ] } }
        """;

    static string? Find(string tree, Uri page)
    {
        using JsonDocument document = JsonDocument.Parse(tree);
        return FlashFrames.FindFrameId(document.RootElement.GetProperty("frameTree"), page);
    }

    [Fact]
    public void The_frame_of_the_content_is_found_by_its_address_never_the_main_document()
    {
        Assert.Equal("JEU", Find(Tree, Game));
        // Adresse changée depuis par la page (même document, même origine) : seul cadre de l'origine.
        Assert.Equal("JEU", Find(Tree, new Uri("http://s81fr.sq.koramgame.com/client/autre.jsp")));
        // Le document principal n'est jamais pris pour un cadre.
        Assert.Null(Find(Tree, new Uri("http://game.fr.demon.koramgame.com/?sid=s158")));
        // Autre origine (port, schéma) ou cadre absent (autre site, autre processus) : introuvable.
        Assert.Null(Find(Tree, new Uri("https://s81fr.sq.koramgame.com/client/game.jsp?user=1&key=abc")));
        Assert.Null(Find(Tree, new Uri("http://s81fr.sq.koramgame.com:8080/client/game.jsp")));
        Assert.Null(Find(Tree, new Uri("http://autre.exemple.com/jeu.html")));
    }

    [Fact]
    public void Two_frames_of_the_same_origin_are_told_apart_by_their_address_only()
    {
        const string twins = """
            { "frame": { "id": "MAIN", "url": "https://page.exemple.com/" },
              "childFrames": [
                { "frame": { "id": "A", "url": "https://jeu.exemple.com/a.html" } },
                { "frame": { "id": "B", "url": "https://jeu.exemple.com/b.html" } } ] }
            """;
        using JsonDocument document = JsonDocument.Parse(twins);
        Assert.Equal("B", FlashFrames.FindFrameId(document.RootElement, new Uri("https://jeu.exemple.com/b.html#ancre")));
        // Adresse inconnue, deux cadres de l'origine : pas de choix au hasard.
        Assert.Null(FlashFrames.FindFrameId(document.RootElement, new Uri("https://jeu.exemple.com/c.html")));
    }

    [Fact]
    public void The_script_checks_the_origin_of_the_frame_and_runs_the_code_in_the_page_world()
    {
        string script = FlashFrames.InFrameScript(Game, "try { __flash__toXML(isSafeFlash(\"32,0,0,270\")) ; } catch (e) { \"<undefined/>\"; }");

        Assert.Contains("if (location.origin !== \"http://s81fr.sq.koramgame.com\") return { ok: false };", script);
        // Code dans le monde de la page, en portée globale (comme NPN_Evaluate), échappé.
        Assert.Contains("(0, eval)(code)", script);
        Assert.Contains("JSON.stringify(\"try { __flash__toXML(isSafeFlash(\\u002232,0,0,270\\u0022))", script);
        Assert.Contains("document.currentScript", script);
        Assert.Contains("element.remove();", script);

        Assert.Equal("http://s81fr.sq.koramgame.com", FlashFrames.Origin(Game));
        Assert.Equal("https://jeu.exemple.com:8443", FlashFrames.Origin(new Uri("https://Jeu.Exemple.com:8443/x")));
        Assert.Equal("http://xn--jeu-dma.exemple.com", FlashFrames.Origin(new Uri("http://jeué.exemple.com/")));
    }

    [Theory]
    [InlineData("""{"result":{"type":"object","value":{"ok":true,"value":"<true/>"}}}""", true, "<true/>")]
    [InlineData("""{"result":{"type":"object","value":{"ok":true,"value":null}}}""", true, null)]
    [InlineData("""{"result":{"type":"object","value":{"ok":false}}}""", false, null)]
    [InlineData("""{"result":{"type":"object"},"exceptionDetails":{"text":"Uncaught"}}""", false, null)]
    public void The_reply_of_the_engine_gives_the_result(string reply, bool ok, string? value)
        => Assert.Equal((ok, value), FlashFrames.ReadResult(reply));

    [Fact]
    public void A_protocol_error_means_the_frame_must_be_looked_up_again()
        => Assert.Null(FlashFrames.ReadResult("""{"code":-32000,"message":"Cannot find context with specified id"}"""));
}
