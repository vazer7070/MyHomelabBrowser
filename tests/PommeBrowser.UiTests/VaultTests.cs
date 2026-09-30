using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using PommeBrowser.Core;
using PommeBrowser.Views;
using PommeBrowser.Views.Pages;

namespace PommeBrowser.UiTests;

/// <summary>Page du coffre et verrouillage automatique.</summary>
public sealed class VaultTests
{
    static PasswordsPage OpenPasswords(MainWindow window)
    {
        window.OpenPasswords();
        TestBrowser.Pump();
        return Assert.IsType<PasswordsPage>(window.SelectedTab?.CurrentPage);
    }

    static TextBox SearchBox(PasswordsPage page)
        => TestBrowser.Find<TextBox>(page).Single(t => t.PlaceholderText?.StartsWith("Rechercher", StringComparison.Ordinal) == true);

    static List<string> ShownSites(PasswordsPage page)
        => TestBrowser.Find<Expander>(page)
            .Select(e => TestBrowser.Find<TextBlock>((Avalonia.Visual)e.Header!).First().Text ?? string.Empty)
            .ToList();

    /// <summary>Régression : la page se reconstruisait sans fin, les fiches clignotaient.</summary>
    [AvaloniaFact]
    public void The_page_stays_still_once_shown()
    {
        TestBrowser.UnlockVaultWith(("https://jeu.exemple.com", "moi"), ("https://mail.exemple.org", "toi"));
        MainWindow window = TestBrowser.OpenWindow();
        PasswordsPage page = OpenPasswords(window);

        TextBox search = SearchBox(page);
        Expander first = TestBrowser.Find<Expander>(page).First();
        TestBrowser.Pump(5);

        Assert.Same(search, SearchBox(page));
        Assert.Same(first, TestBrowser.Find<Expander>(page).First());
        window.Close();
    }

    [AvaloniaFact]
    public void Searching_filters_the_list_without_losing_the_field()
    {
        TestBrowser.UnlockVaultWith(("https://jeu.exemple.com", "moi"), ("https://mail.exemple.org", "toi"));
        MainWindow window = TestBrowser.OpenWindow();
        PasswordsPage page = OpenPasswords(window);
        Assert.Equal(2, ShownSites(page).Count);

        TextBox search = SearchBox(page);
        search.Focus();
        window.KeyTextInput("jeu");
        TestBrowser.Pump();

        Assert.Same(search, SearchBox(page));
        Assert.True(search.IsFocused);
        Assert.Equal("jeu.exemple.com", Assert.Single(ShownSites(page)));
        window.Close();
    }

    [AvaloniaFact]
    public void A_card_opens_with_one_click_and_stays_open()
    {
        TestBrowser.UnlockVaultWith(("https://jeu.exemple.com", "moi"));
        MainWindow window = TestBrowser.OpenWindow();
        PasswordsPage page = OpenPasswords(window);

        Expander card = TestBrowser.Find<Expander>(page).Single();
        ToggleButton toggle = TestBrowser.Find<ToggleButton>(card).First();
        TestBrowser.Click(toggle);
        TestBrowser.Pump(5);

        Assert.Same(card, TestBrowser.Find<Expander>(page).Single());
        Assert.True(card.IsExpanded);
        window.Close();
    }

    [AvaloniaFact]
    public void The_vault_locks_itself_after_the_chosen_delay()
    {
        TestBrowser.UnlockVaultWith(("https://jeu.exemple.com", "moi"));
        Vault vault = TestBrowser.App.Vault;
        int saved = TestBrowser.App.Settings.VaultAutoLockMinutes;
        try
        {
            TestBrowser.App.Settings.VaultAutoLockMinutes = 15;
            vault.Touch();
            vault.CheckAutoLock(DateTime.UtcNow.AddMinutes(14));
            Assert.True(vault.IsUnlocked);
            vault.CheckAutoLock(DateTime.UtcNow.AddMinutes(16));
            Assert.False(vault.IsUnlocked);

            TestBrowser.UnlockVaultWith(("https://jeu.exemple.com", "moi"));
            TestBrowser.App.Settings.VaultAutoLockMinutes = 0;
            vault.CheckAutoLock(DateTime.UtcNow.AddDays(3));
            Assert.True(vault.IsUnlocked);
        }
        finally
        {
            TestBrowser.App.Settings.VaultAutoLockMinutes = saved;
        }
    }

    [AvaloniaFact]
    public void A_locked_vault_shows_the_unlock_and_reset_buttons()
    {
        TestBrowser.UnlockVaultWith(("https://jeu.exemple.com", "moi"));
        MainWindow window = TestBrowser.OpenWindow();
        PasswordsPage page = OpenPasswords(window);

        TestBrowser.App.Vault.Lock();
        TestBrowser.Pump();

        List<string?> buttons = TestBrowser.Find<Button>(page).Select(b => b.Content as string).ToList();
        Assert.Contains("Déverrouiller…", buttons);
        Assert.Contains("Mot de passe oublié…", buttons);
        Assert.Empty(TestBrowser.Find<Expander>(page));
        window.Close();
    }
}
