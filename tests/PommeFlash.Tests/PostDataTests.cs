using System.Text;
using PommeFlash.Host;

namespace PommeFlash.Tests;

/// <summary>Envoi du module (NPN_PostURL) : en-têtes de Flash séparés du corps, longueur annoncée respectée.</summary>
public sealed class PostDataTests
{
    static PostData Parse(string data) => PostData.Parse(Encoding.ASCII.GetBytes(data));

    [Fact]
    public void Headers_written_by_the_module_are_separated_from_the_body()
    {
        PostData post = Parse("Content-Type: application/x-www-form-urlencoded\r\nContent-Length: 14\r\n\r\nuser=1&site=fr");
        Assert.Equal("user=1&site=fr", Encoding.ASCII.GetString(post.Body));
        Assert.Equal("application/x-www-form-urlencoded", post.Header("content-type"));
        Assert.Equal(0, post.Dropped);
    }

    [Fact]
    public void The_body_stops_at_the_announced_length_like_a_server_reads_it()
    {
        // Zéro final d'une chaîne C compté dans la taille du tampon : sans cette coupe, le dernier
        // champ (site) arriverait au serveur avec un caractère en trop.
        PostData post = Parse("Content-Type: application/x-www-form-urlencoded\r\nContent-Length: 14\r\n\r\nuser=1&site=fr\0");
        Assert.Equal("user=1&site=fr", Encoding.ASCII.GetString(post.Body));
        Assert.Equal(1, post.Dropped);

        // Longueur annoncée absente, invalide ou plus grande : le corps entier.
        Assert.Equal(15, Parse("Content-Type: text/plain\r\n\r\nuser=1&site=fr\0").Body.Length);
        Assert.Equal(5, Parse("Content-Length: abc\r\n\r\nhello").Body.Length);
        Assert.Equal(5, Parse("Content-Length: 99\r\n\r\nhello").Body.Length);
    }

    [Fact]
    public void Headers_ending_with_bare_line_feeds_are_separated_like_firefox_does()
    {
        // Demon Slayer : en-têtes finis par \n seules ; lus comme du corps, ils partaient collés
        // devant le formulaire, et le serveur n'y trouvait plus le champ « site ».
        const string form = "user=1207917868&key=57D95D5F&site=fr_0158&p2=0&rid=1";
        PostData post = Parse($"Content-Type: application/x-www-form-urlencoded\nContent-Length: {form.Length}\n\n{form}");
        Assert.Equal(form, Encoding.ASCII.GetString(post.Body));
        Assert.Equal("application/x-www-form-urlencoded", post.Header("Content-Type"));
        Assert.Equal(form.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), post.Header("Content-Length"));
        Assert.Equal("en-têtes (fins de ligne \\n)", post.Layout);

        // Mélange de fins de ligne, en-tête Content-Length en minuscules et en premier.
        PostData mixed = Parse("content-length: 3\r\nContent-Type: text/plain\n\nabc");
        Assert.Equal("abc", Encoding.ASCII.GetString(mixed.Body));
        Assert.Equal("text/plain", mixed.Header("content-type"));
    }

    [Fact]
    public void A_blank_first_line_means_no_headers_as_npapi_says()
    {
        PostData post = Parse("\nuser=1&site=fr");
        Assert.Equal("user=1&site=fr", Encoding.ASCII.GetString(post.Body));
        Assert.Empty(post.Headers);
        Assert.Equal("sans en-têtes (ligne vide en tête)", post.Layout);
        Assert.Equal("user=1&site=fr", Encoding.ASCII.GetString(Parse("\r\nuser=1&site=fr").Body));
    }

    [Fact]
    public void Data_without_a_header_block_is_all_body()
    {
        Assert.Equal("a=1&b=2", Encoding.ASCII.GetString(Parse("a=1&b=2").Body));
        Assert.Empty(Parse("a=1&b=2").Headers);
        Assert.Equal("not a header\r\n\r\nbody", Encoding.ASCII.GetString(Parse("not a header\r\n\r\nbody").Body));
    }
}
