using System.Globalization;
using PommeBrowser.Legacy;

namespace PommeBrowser.UiTests;

/// <summary>Chargements du lecteur annoncés avec les langues de la page, comme Chromium les annonce.</summary>
public sealed class FlashHostArgumentsTests
{
    [Theory]
    [InlineData("fr-FR", "fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7")]
    [InlineData("en-US", "en-US,en;q=0.9")]
    [InlineData("fr", "fr,en-US;q=0.9,en;q=0.8")]
    [InlineData("de-CH", "de-CH,de;q=0.9,en-US;q=0.8,en;q=0.7")]
    public void Loads_announce_the_languages_of_the_interface_then_english(string culture, string header)
        => Assert.Equal(header, FlashHostProcess.AcceptLanguage(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void An_invariant_language_announces_nothing()
        => Assert.Null(FlashHostProcess.AcceptLanguage(CultureInfo.InvariantCulture));
}
