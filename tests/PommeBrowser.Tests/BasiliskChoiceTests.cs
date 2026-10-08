using System.Text.Json;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Tests;

/// <summary>Réglage « Utiliser Basilisk » : repris de l'ancienne case, gardé ensuite.</summary>
public sealed class BasiliskChoiceTests
{
    static BrowserSettings Read(string json)
    {
        BrowserSettings settings = JsonSerializer.Deserialize<BrowserSettings>(json)!;
        settings.ResolveBasiliskChoice();
        return settings;
    }

    [Fact]
    public void Unticking_the_old_Basilisk_box_now_means_never_Basilisk()
    {
        // « Ouvrir dans Basilisk les contenus que Ruffle ne sait pas lire » décoché.
        BrowserSettings settings = Read("""{"FlashAutoFallback":false}""");

        Assert.False(settings.BasiliskEnabled);
        // La bascule d'office reste active : elle mène au moteur intégré.
        Assert.True(settings.FlashAutoFallback);
    }

    [Fact]
    public void Basilisk_stays_allowed_by_default_and_the_new_choice_is_kept()
    {
        Assert.True(Read("""{"FlashAutoFallback":true}""").BasiliskEnabled);
        Assert.True(Read("{}").BasiliskEnabled);

        BrowserSettings chosen = Read("""{"FlashAutoFallback":false,"BasiliskEnabled":true}""");
        Assert.True(chosen.BasiliskEnabled);
        Assert.False(chosen.FlashAutoFallback);

        BrowserSettings off = Read("""{"FlashAutoFallback":true,"BasiliskEnabled":false}""");
        Assert.False(off.BasiliskEnabled);
        Assert.True(off.FlashAutoFallback);
    }
}
