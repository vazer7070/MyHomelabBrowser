using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using PommeBrowser;
using PommeBrowser.Views;

namespace PommeBrowser.UiTests;

/// <summary>Bouton ⚡ : un éclair par moteur Flash (Ruffle, moteur intégré, Basilisk), dans les deux thèmes.</summary>
public sealed class FlashButtonTests
{
    [AvaloniaFact]
    public void Each_flash_engine_has_its_own_lightning_and_colour_in_both_themes()
    {
        MainWindow window = TestBrowser.OpenWindow();
        Application application = Application.Current!;

        // Trois formes différentes.
        var shapes = new[] { "IconFlash", "IconFlashFilled", "IconFlashFramed" }
            .Select(key => Assert.IsAssignableFrom<Geometry>(window.FindResource(key)))
            .ToList();
        // Formes comparées par les points qu'elles remplissent (grille de l'icône 20 × 20).
        Assert.Equal(3, shapes.Select(Coverage).Distinct().Count());
        Assert.All(shapes, shape => Assert.True(shape.Bounds.Width > 10 && shape.Bounds.Height > 10));

        // Trois couleurs, distinctes dans chaque thème.
        foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var colours = new[] { "RuffleBrush", "FlashPlayerBrush", "BasiliskBrush" }
                .Select(key => application.TryGetResource(key, theme, out object? value) ? ((ISolidColorBrush)value!).Color : default)
                .ToList();
            Assert.DoesNotContain(default(Color), colours);
            Assert.Equal(3, colours.Distinct().Count());
        }

        // Page d'accueil : pas de Flash, pas de bouton ; l'éclair est celui de Ruffle.
        var button = window.FindControl<Button>("LegacyButton")!;
        Assert.False(button.IsVisible);
        Assert.Equal(FlashEngine.Ruffle, window.SelectedTab!.FlashEngine);
        window.Close();
    }

    [AvaloniaFact]
    public void The_integrated_engine_chosen_on_a_page_is_kept_for_the_whole_site()
    {
        BrowserApp app = TestBrowser.App;

        // Choisi sur l'accueil du jeu : sa page de jeu (autre sous-domaine) démarre aussi avec lui,
        // sans que Ruffle s'y connecte d'abord.
        app.PreferIntegratedFlash("fr.demon.koramgame.com", true);
        Assert.Contains("koramgame.com", app.SessionIntegratedSites);
        Assert.True(app.PrefersIntegratedFlash("game.fr.demon.koramgame.com"));
        Assert.True(app.PrefersIntegratedFlash("s81fr.sq.koramgame.com"));
        Assert.True(app.PrefersIntegratedFlash("koramgame.com"));
        // Un autre site, même s'il finit pareil : non.
        Assert.False(app.PrefersIntegratedFlash("notkoramgame.com"));
        Assert.False(app.PrefersIntegratedFlash("koramgame.com.exemple.fr"));
        Assert.False(app.PrefersIntegratedFlash(null));

        // Retour à Ruffle depuis n'importe quelle page du site : tout le site.
        app.PreferIntegratedFlash("game.fr.demon.koramgame.com", false);
        Assert.False(app.PrefersIntegratedFlash("fr.demon.koramgame.com"));
        Assert.Empty(app.SessionIntegratedSites);
    }

    static string Coverage(Geometry shape)
    {
        var points = new System.Text.StringBuilder();
        for (double y = 0.5; y < 20; y++)
            for (double x = 0.5; x < 20; x++)
                points.Append(shape.FillContains(new Point(x, y)) ? '#' : '.');
        return points.ToString();
    }
}
