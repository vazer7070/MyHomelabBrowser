using MyHomelabBrowser.classes.Security;
using System.Text;

namespace PommeBrowser.Tests;

public class TotpTests
{
    // Vecteurs de la RFC 6238 (annexe B), clé ASCII « 12345678901234567890 ».
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Rfc6238_sha1_vectors(long unixTime, string expected)
    {
        var parameters = new TotpParameters(Encoding.ASCII.GetBytes("12345678901234567890"), Digits: 8);
        Assert.Equal(expected, Totp.Generate(parameters, DateTimeOffset.FromUnixTimeSeconds(unixTime)));
    }

    [Fact]
    public void Rfc6238_sha256_vector()
    {
        var parameters = new TotpParameters(Encoding.ASCII.GetBytes("12345678901234567890123456789012"), Digits: 8, Algorithm: "SHA256");
        Assert.Equal("46119246", Totp.Generate(parameters, DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Fact]
    public void Base32_keys_with_spaces_are_accepted()
    {
        string key = Totp.EncodeBase32(Encoding.ASCII.GetBytes("12345678901234567890"));
        string spaced = string.Join(' ', Enumerable.Range(0, key.Length / 4).Select(i => key.Substring(i * 4, 4))).ToLowerInvariant();

        Assert.True(Totp.TryParse(spaced, out TotpParameters parameters, out _));
        Assert.Equal("287082", Totp.Generate(parameters, DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Fact]
    public void Otpauth_uri_is_parsed()
    {
        const string uri = "otpauth://totp/Proxmox:root%40pam?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Proxmox&digits=8&period=30";

        Assert.True(Totp.TryParse(uri, out TotpParameters parameters, out _));
        Assert.Equal(8, parameters.Digits);
        Assert.Equal("Proxmox", parameters.Issuer);
        Assert.Equal("root@pam", parameters.Account);
        Assert.Equal("94287082", Totp.Generate(parameters, DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas une clé !")]
    [InlineData("ABC")]
    [InlineData("otpauth://hotp/x?secret=GEZDGNBVGY3TQOJQ")]
    public void Invalid_keys_are_rejected(string input)
    {
        Assert.False(Totp.TryParse(input, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Seconds_remaining_counts_down_within_the_period()
    {
        var parameters = new TotpParameters(new byte[10]);
        Assert.Equal(30, Totp.SecondsRemaining(parameters, DateTimeOffset.FromUnixTimeSeconds(60)));
        Assert.Equal(1, Totp.SecondsRemaining(parameters, DateTimeOffset.FromUnixTimeSeconds(89)));
    }

    [Fact]
    public void Codes_are_grouped_for_display()
        => Assert.Equal("123 456", Totp.FormatForDisplay("123456"));
}
