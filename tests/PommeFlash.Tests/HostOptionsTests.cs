using PommeFlash.Host;

namespace PommeFlash.Tests;

/// <summary>Paramètres donnés au module (NPP_New) à partir de ceux de l'élément de la page.</summary>
public sealed class HostOptionsTests : IDisposable
{
    readonly string _plugin = Path.GetTempFileName();

    public void Dispose() => File.Delete(_plugin);

    HostOptions Parse(params string[] extra)
        => HostOptions.Parse(new[] { "--plugin", _plugin, "--swf", "https://jeu.exemple.com/client.swf?v=59", "--page", "https://jeu.exemple.com/" }
            .Concat(extra).ToList());

    static string? Argument(HostOptions options, string name)
        => options.PluginArguments().LastOrDefault(p => p.Key == name).Value;

    [Theory]
    [InlineData("direct", "direct")]
    [InlineData("GPU", "gpu")]
    [InlineData(" Direct ", "direct")]
    [InlineData("opaque", "window")]
    [InlineData("transparent", "window")]
    [InlineData("window", "window")]
    [InlineData("n'importe quoi", "window")]
    public void Windowed_render_modes_of_the_page_are_kept(string requested, string given)
    {
        HostOptions options = Parse("--param", "wmode=" + requested);

        Assert.Equal(given, Argument(options, "wmode"));
        Assert.Single(options.PluginArguments(), p => p.Key.Equals("wmode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Without_render_mode_the_module_draws_in_its_window()
    {
        Assert.Equal("window", Argument(Parse(), "wmode"));
    }

    [Fact]
    public void The_module_sees_the_identity_of_Basilisk_unless_another_is_given()
    {
        string agent = Parse().UserAgent;
        Assert.StartsWith("Mozilla/5.0 (Windows NT ", agent, StringComparison.Ordinal);
        Assert.Contains("Gecko/20100101 Goanna/", agent, StringComparison.Ordinal);
        Assert.Contains(" Firefox/", agent, StringComparison.Ordinal);
        Assert.Contains(" Basilisk/", agent, StringComparison.Ordinal);
        Assert.Contains(Environment.Is64BitProcess ? "; Win64; x64;" : "; WOW64;", agent, StringComparison.Ordinal);

        Assert.Equal("Autre/1.0", Parse("--user-agent", "Autre/1.0").UserAgent);
    }

    [Fact]
    public void Element_attributes_and_parameters_are_given_like_a_browser()
    {
        HostOptions options = Parse("--flashvars", "sClientAbsoluteUrl=", "--id", "EmpireClient",
            "--width", "1400", "--height", "774",
            "--param", "allowscriptaccess=always", "--param", "quality=best", "--param", "scale=noscale");

        Assert.Equal("https://jeu.exemple.com/client.swf?v=59", Argument(options, "src"));
        Assert.Equal("EmpireClient", Argument(options, "id"));
        Assert.Equal("EmpireClient", Argument(options, "name"));
        Assert.Equal("1400", Argument(options, "width"));
        Assert.Equal("sClientAbsoluteUrl=", Argument(options, "flashvars"));
        // Les paramètres de la page remplacent les valeurs par défaut.
        Assert.Equal("always", Argument(options, "allowscriptaccess"));
        Assert.Equal("best", Argument(options, "quality"));
        Assert.Single(options.PluginArguments(), p => p.Key == "quality");
        Assert.Equal("noscale", Argument(options, "scale"));
    }

    [Fact]
    public void Script_access_defaults_to_the_same_domain()
    {
        Assert.Equal("sameDomain", Argument(Parse(), "allowscriptaccess"));
    }
}
