using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
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

    static string Coverage(Geometry shape)
    {
        var points = new System.Text.StringBuilder();
        for (double y = 0.5; y < 20; y++)
            for (double x = 0.5; x < 20; x++)
                points.Append(shape.FillContains(new Point(x, y)) ? '#' : '.');
        return points.ToString();
    }
}
