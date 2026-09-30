using MyHomelabBrowser.classes.Profiles.Credentials;

namespace PommeBrowser.Tests;

/// <summary>Coffre des mots de passe : déverrouillage, causes d'échec, changement de mot de passe, réinitialisation.</summary>
public sealed class CredentialVaultTests : IDisposable
{
    const string Password = "Pâques@2026#€";

    readonly string _directory = Path.Combine(Path.GetTempPath(), "pomme-coffre-" + Guid.NewGuid().ToString("N"));

    string VaultPath => Path.Combine(_directory, "vault.json.enc");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    CredentialVaultService NewService(string? path = null)
    {
        string target = path ?? VaultPath;
        return new CredentialVaultService(() => target);
    }

    CredentialVaultService CreateVaultWithOneEntry()
    {
        CredentialVaultService vault = NewService();
        Assert.True(vault.TryInitializeNewVault(Password));
        vault.Upsert("https://jeu.exemple.com", "moi", "secret-du-site", formAction: null);
        vault.Lock();
        return vault;
    }

    [Fact]
    public void The_right_password_unlocks_a_new_instance()
    {
        CreateVaultWithOneEntry();

        CredentialVaultService reopened = NewService();
        Assert.True(reopened.TryUnlock(Password, out VaultUnlockFailure failure));
        Assert.Equal(VaultUnlockFailure.None, failure);
        CredentialEntry entry = Assert.Single(reopened.GetAll());
        Assert.Equal("secret-du-site", entry.Password);
    }

    [Fact]
    public void A_wrong_password_is_reported_as_such()
    {
        CreateVaultWithOneEntry();

        CredentialVaultService reopened = NewService();
        Assert.False(reopened.TryUnlock("Paques@2026#€", out VaultUnlockFailure failure));
        Assert.Equal(VaultUnlockFailure.WrongPassword, failure);
        Assert.False(reopened.IsUnlocked);
    }

    [Fact]
    public void An_unreadable_file_is_not_a_wrong_password_and_does_not_lock()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(VaultPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        CredentialVaultService vault = NewService();
        for (int i = 0; i < 5; i++)
        {
            Assert.False(vault.TryUnlock(Password, out VaultUnlockFailure failure));
            Assert.Equal(VaultUnlockFailure.Unreadable, failure);
        }
        Assert.NotNull(vault.LastUnlockError);
        Assert.Null(vault.UnlockAvailableAtUtc);
    }

    [Fact]
    public void Repeated_wrong_passwords_lock_the_vault_for_a_while()
    {
        CreateVaultWithOneEntry();

        CredentialVaultService vault = NewService();
        for (int i = 0; i < 3; i++)
            vault.TryUnlock("mauvais-" + i, out _);

        Assert.False(vault.TryUnlock(Password, out VaultUnlockFailure failure));
        Assert.Equal(VaultUnlockFailure.Locked, failure);
        Assert.NotNull(vault.UnlockAvailableAtUtc);
    }

    [Fact]
    public void After_a_password_change_only_the_new_password_opens_the_vault()
    {
        CreateVaultWithOneEntry();
        CredentialVaultService vault = NewService();
        Assert.True(vault.TryChangeVaultPassword(Password, "nouveau-mot-de-passe"));
        vault.Lock();

        Assert.False(NewService().TryUnlock(Password, out VaultUnlockFailure failure));
        Assert.Equal(VaultUnlockFailure.WrongPassword, failure);
        Assert.True(NewService().TryUnlock("nouveau-mot-de-passe"));
    }

    [Fact]
    public void Reset_sets_the_old_vault_aside_and_allows_a_new_one()
    {
        CredentialVaultService vault = CreateVaultWithOneEntry();

        string? archived = vault.ResetVault();

        Assert.NotNull(archived);
        Assert.False(vault.VaultExists);
        Assert.True(vault.TryInitializeNewVault("un-autre-mot-de-passe"));
        Assert.Empty(vault.GetAll());

        // L'ancien coffre n'est pas perdu : il s'ouvre encore avec son mot de passe.
        CredentialVaultService old = NewService(archived);
        Assert.True(old.TryUnlock(Password));
        Assert.Equal("secret-du-site", Assert.Single(old.GetAll()).Password);
    }

    [Fact]
    public void Reset_without_a_vault_does_nothing()
    {
        Assert.Null(NewService().ResetVault());
    }
}
