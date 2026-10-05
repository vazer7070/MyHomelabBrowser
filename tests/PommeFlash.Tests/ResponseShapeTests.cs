using System.Text;
using PommeFlash.Host;

namespace PommeFlash.Tests;

/// <summary>
/// Forme des petites réponses notée au journal (connexion de Demon Slayer : « HTTP 200 text/html »
/// sans rien de plus) : ce qu'elles sont, jamais leurs valeurs.
/// </summary>
public sealed class ResponseShapeTests
{
    static string Describe(string body) => ResponseShape.Describe(Encoding.UTF8.GetBytes(body));

    [Theory]
    [InlineData("0", "nombre seul : 0")]
    [InlineData(" -3\r\n", "nombre seul : -3")]
    [InlineData("", "vide")]
    [InlineData("  \n", "blancs seulement")]
    public void A_return_code_is_noted(string body, string shape)
        => Assert.Equal(shape, Describe(body));

    [Fact]
    public void Values_never_appear_only_the_names()
    {
        string xml = Describe("<?xml version=\"1.0\"?>\n<!-- serveur -->\n<login result=\"fail\" key=\"SECRET\"><msg>SECRET</msg></login>");
        Assert.Equal("XML ou HTML, élément <login>", xml);
        Assert.Equal("XML ou HTML, élément <html>", Describe("<!DOCTYPE html><html><body>SECRET</body></html>"));

        string json = Describe("{\"code\":-2,\"token\":\"SECRET\",\"user\":{\"name\":\"SECRET\"}}");
        Assert.Equal("JSON, clés : code, token, user", json);
        Assert.Equal("JSON, tableau de 2 éléments", Describe("[\"SECRET\", 1]"));
        Assert.Equal("JSON incomplet ou invalide", Describe("{\"code\":"));

        string form = Describe("result=1&session%5Fid=SECRET&server=SECRET");
        Assert.Equal("formulaire, champs : result, session_id, server", form);

        // Long nombre (jeton numérique) ou texte libre : rien de leur contenu.
        Assert.Equal("texte (12 caractères)", Describe("123456789012"));
        Assert.Equal("texte (16 caractères)", Describe("SECRET et SECRET"));
        Assert.DoesNotContain("SECRET", xml + json + form);
    }

    [Fact]
    public void Binary_content_is_not_read_as_text()
        => Assert.Equal("binaire", ResponseShape.Describe(new byte[] { 0x0A, 0xFF, 0xFE, 0x00, 0x80 }));

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("application/xml", true)]
    [InlineData("application/json", true)]
    [InlineData(null, true)]
    [InlineData("application/x-shockwave-flash", false)]
    [InlineData("image/png", false)]
    public void Only_text_responses_are_described(string? mediaType, bool described)
        => Assert.Equal(described, ResponseShape.IsText(mediaType));
}
