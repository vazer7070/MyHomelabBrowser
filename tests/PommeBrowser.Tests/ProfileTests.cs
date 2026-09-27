using MyHomelabBrowser.classes.Profiles;

namespace PommeBrowser.Tests;

public class ProfileNameRulesTests
{
    [Theory]
    [InlineData("Raphaël")]
    [InlineData("homelab-01")]
    [InlineData("Maison principale")]
    [InlineData("a.b_c")]
    public void Valid_names_are_accepted(string name)
        => Assert.True(ProfileNameRules.TryValidate(name, out _));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../autre")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("nom:flux")]
    [InlineData("CON")]
    [InlineData("default")]
    [InlineData(".cache")]
    [InlineData("")]
    [InlineData("   ")]
    public void Dangerous_or_reserved_names_are_refused(string name)
    {
        Assert.False(ProfileNameRules.TryValidate(name, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Names_longer_than_the_limit_are_refused()
        => Assert.False(ProfileNameRules.TryValidate(new string('a', ProfileNameRules.MaxLength + 1), out _));
}

public class PasswordHasherTests
{
    [Fact]
    public void Hash_then_verify_round_trips()
    {
        var (hash, salt) = PasswordHasher.Hash("correct horse");

        Assert.True(PasswordHasher.Verify("correct horse", hash, salt, PasswordHasher.CurrentIterations));
        Assert.False(PasswordHasher.Verify("Correct horse", hash, salt, PasswordHasher.CurrentIterations));
    }

    [Fact]
    public void Salts_are_unique()
    {
        var first = PasswordHasher.Hash("secret");
        var second = PasswordHasher.Hash("secret");

        Assert.NotEqual(first.salt, second.salt);
        Assert.NotEqual(first.hash, second.hash);
    }

    [Fact]
    public void Hashes_made_with_other_iteration_counts_do_not_verify()
    {
        var (hash, salt) = PasswordHasher.Hash("secret");
        Assert.False(PasswordHasher.Verify("secret", hash, salt, PasswordHasher.LegacyIterations));
    }

    [Fact]
    public void Malformed_hashes_are_rejected()
        => Assert.False(PasswordHasher.Verify("secret", new byte[3], new byte[16]));
}
