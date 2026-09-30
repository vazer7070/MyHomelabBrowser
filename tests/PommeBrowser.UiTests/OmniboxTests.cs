using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PommeBrowser.Views;

namespace PommeBrowser.UiTests;

/// <summary>Barre d'adresse : les propositions répondent au clic.</summary>
public sealed class OmniboxTests
{
    /// <summary>
    /// Régression : l'appui sur une proposition retirait le focus du champ, ce qui fermait la
    /// liste avant le relâchement ; le clic ne faisait rien.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_a_suggestion_runs_it()
    {
        MainWindow window = TestBrowser.OpenWindow();
        window.OpenHistory();
        BrowserTab history = window.SelectedTab!;
        window.NewTab(null, select: true);
        TestBrowser.Pump();
        Assert.NotSame(history, window.SelectedTab);

        // « @ » : aller à un onglet ouvert (aucune page web n'est chargée).
        window.AddressBar.Focus();
        window.KeyTextInput("@" + history.Title[..4]);
        TestBrowser.Pump();
        Assert.True(window.SuggestionsPopup.IsOpen);

        var item = (Control)window.SuggestionList.ContainerFromIndex(0)!;
        (TopLevel root, Point point) = TestBrowser.CenterOf(item);
        root.MouseDown(point, MouseButton.Left);
        TestBrowser.Pump();
        Assert.True(window.SuggestionsPopup.IsOpen);
        Assert.True(window.AddressBar.IsFocused);

        root.MouseUp(point, MouseButton.Left);
        TestBrowser.Pump();
        Assert.Same(history, window.SelectedTab);
        Assert.False(window.SuggestionsPopup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_in_the_address_bar_proposes_the_matching_tabs_only()
    {
        MainWindow window = TestBrowser.OpenWindow();
        window.OpenSettings();
        window.NewTab(null, select: true);
        TestBrowser.Pump();

        window.AddressBar.Focus();
        window.KeyTextInput("@Param");
        TestBrowser.Pump();

        OmniboxEntry entry = Assert.Single(window.SuggestionList.Items.OfType<OmniboxEntry>());
        Assert.StartsWith("Param", entry.Primary, StringComparison.Ordinal);
        window.Close();
    }
}
