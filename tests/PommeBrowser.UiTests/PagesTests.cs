using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PommeBrowser.Views;
using PommeBrowser.Views.Pages;

namespace PommeBrowser.UiTests;

/// <summary>Chaque page de PommeBrowser s'ouvre, et ses réglages répondent.</summary>
public sealed class PagesTests
{
    [AvaloniaFact]
    public void The_window_opens_on_the_home_page()
    {
        MainWindow window = TestBrowser.OpenWindow();
        Assert.Equal(TabPage.Home, window.SelectedTab?.Page);
        Assert.IsType<HomePage>(window.SelectedTab?.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_internal_page_opens()
    {
        MainWindow window = TestBrowser.OpenWindow();

        window.OpenHistory();
        TestBrowser.Pump();
        Assert.IsType<HistoryPage>(window.SelectedTab?.CurrentPage);

        window.OpenFavorites();
        TestBrowser.Pump();
        Assert.IsType<FavoritesPage>(window.SelectedTab?.CurrentPage);

        window.OpenPasswords();
        TestBrowser.Pump();
        Assert.IsType<PasswordsPage>(window.SelectedTab?.CurrentPage);

        window.OpenDiagnostics();
        TestBrowser.Pump();
        Assert.IsType<DiagnosticsPage>(window.SelectedTab?.CurrentPage);
        // Moteur Flash intégré : réglage, hôtes, modules, lecteurs, dernier arrêt.
        Assert.Contains(TestBrowser.Find<TextBlock>(window), t => t.Text is "Moteur Flash intégré" or "Integrated Flash engine");
        Assert.Contains(TestBrowser.Find<TextBlock>(window), t => t.Text is "Dernier arrêt inattendu" or "Last unexpected stop");

        window.OpenReport();
        TestBrowser.Pump();
        Assert.IsType<ReportPage>(window.SelectedTab?.CurrentPage);

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("general")]
    [InlineData("downloads")]
    [InlineData("history")]
    [InlineData("updates")]
    [InlineData("adblock")]
    [InlineData("flash")]
    public void Every_settings_section_opens(string section)
    {
        MainWindow window = TestBrowser.OpenWindow();
        window.OpenSettings(section);
        TestBrowser.Pump();
        Assert.IsType<SettingsPage>(window.SelectedTab?.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public void The_automatic_Basilisk_fallback_can_be_turned_off()
    {
        MainWindow window = TestBrowser.OpenWindow();
        window.OpenSettings("flash");
        TestBrowser.Pump();
        bool before = TestBrowser.App.Settings.FlashAutoFallback;

        CheckBox check = TestBrowser.Find<CheckBox>(window.SelectedTab!.CurrentPage!)
            .Single(c => c.Content is TextBlock { Text: { } text } && text.StartsWith("Ouvrir dans Basilisk les contenus", StringComparison.Ordinal));
        Assert.Equal(before, check.IsChecked);

        TestBrowser.Click(check);
        Assert.Equal(!before, TestBrowser.App.Settings.FlashAutoFallback);
        TestBrowser.Click(check);
        Assert.Equal(before, TestBrowser.App.Settings.FlashAutoFallback);
        window.Close();
    }
}
