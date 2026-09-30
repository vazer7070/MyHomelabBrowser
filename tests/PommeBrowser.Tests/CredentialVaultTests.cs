using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    // ---------------------------------------------------------------
    // Argon2id
    // ---------------------------------------------------------------

    static bool IsArgon2File(string path) => File.ReadAllBytes(path).AsSpan().StartsWith("PVK2"u8);

    /// <summary>Vecteur de test de la RFC 9106 (5.3, Argon2id).</summary>
    [Fact]
    public void Argon2id_matches_the_RFC_9106_test_vector()
    {
        byte[] tag = VaultKeyDerivation.Argon2id(
            Enumerable.Repeat((byte)1, 32).ToArray(),
            Enumerable.Repeat((byte)2, 16).ToArray(),
            memoryKiB: 32, iterations: 3, parallelism: 4, length: 32,
            secret: Enumerable.Repeat((byte)3, 8).ToArray(),
            associatedData: Enumerable.Repeat((byte)4, 12).ToArray());

        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexString(tag).ToLowerInvariant());
    }

    [Fact]
    public void A_new_vault_is_protected_with_Argon2id()
    {
        CreateVaultWithOneEntry();
        Assert.True(IsArgon2File(VaultPath));
    }

    /// <summary>Coffre au format d'avant (PBKDF2), tel que l'écrivaient les versions précédentes.</summary>
    void WritePbkdf2Vault(string path, string password, string site, string user, string secret)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA256, 32);
        string json = JsonSerializer.Serialize(new
        {
            Credentials = new[] { new { Host = site, Username = user, Password = secret, UpdatedAt = DateTime.UtcNow } },
            Policies = Array.Empty<object>()
        });
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(salt.Length);
        writer.Write(salt);
        writer.Write(nonce.Length);
        writer.Write(nonce);
        writer.Write(tag.Length + cipher.Length);
        writer.Write(tag);
        writer.Write(cipher);
    }

    [Fact]
    public void An_older_PBKDF2_vault_opens_and_is_converted_to_Argon2id()
    {
        WritePbkdf2Vault(VaultPath, Password, "https://jeu.exemple.com", "moi", "secret-du-site");
        File.Copy(VaultPath, VaultPath + ".bak");
        Assert.False(IsArgon2File(VaultPath));

        CredentialVaultService vault = NewService();
        Assert.True(vault.TryUnlock(Password, out VaultUnlockFailure failure));
        Assert.Equal(VaultUnlockFailure.None, failure);
        Assert.Equal("secret-du-site", Assert.Single(vault.GetAll()).Password);

        // Converti sans rien demander : coffre et sauvegarde, même mot de passe.
        Assert.True(IsArgon2File(VaultPath));
        Assert.True(IsArgon2File(VaultPath + ".bak"));
        CredentialVaultService reopened = NewService();
        Assert.True(reopened.TryUnlock(Password));
        Assert.Equal("secret-du-site", Assert.Single(reopened.GetAll()).Password);
        Assert.False(NewService().TryUnlock("Paques@2026#€", out VaultUnlockFailure wrong));
        Assert.Equal(VaultUnlockFailure.WrongPassword, wrong);
    }

    [Fact]
    public void A_wrong_password_leaves_an_older_vault_untouched()
    {
        WritePbkdf2Vault(VaultPath, Password, "https://jeu.exemple.com", "moi", "secret-du-site");
        byte[] before = File.ReadAllBytes(VaultPath);

        Assert.False(NewService().TryUnlock("mauvais", out VaultUnlockFailure failure));

        Assert.Equal(VaultUnlockFailure.WrongPassword, failure);
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
    }

    [Fact]
    public void Excessive_Argon2id_parameters_make_the_file_unreadable_not_slow()
    {
        CreateVaultWithOneEntry();
        byte[] bytes = File.ReadAllBytes(VaultPath);
        BitConverter.GetBytes(64 * 1024 * 1024).CopyTo(bytes, 4); // 64 Gio demandés
        File.WriteAllBytes(VaultPath, bytes);
        File.Delete(VaultPath + ".bak");

        Assert.False(NewService().TryUnlock(Password, out VaultUnlockFailure failure));
        Assert.Equal(VaultUnlockFailure.Unreadable, failure);
    }

    [Fact]
    public void Changed_Argon2id_parameters_are_detected()
    {
        CreateVaultWithOneEntry();
        byte[] bytes = File.ReadAllBytes(VaultPath);
        BitConverter.GetBytes(2).CopyTo(bytes, 8); // passes : 3 → 2
        File.WriteAllBytes(VaultPath, bytes);

        Assert.False(NewService().TryUnlock(Password, out VaultUnlockFailure failure));
        Assert.Equal(VaultUnlockFailure.WrongPassword, failure);
    }
}
