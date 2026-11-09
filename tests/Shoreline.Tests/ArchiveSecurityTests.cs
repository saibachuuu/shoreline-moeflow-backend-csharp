using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

public sealed class ArchiveSecurityTests
{
    // Generated offline by Python cryptography.Fernet using only a fixed test
    // key, timestamp and IV. None of these are deployment credentials.
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string PythonToken = "fernet:gAAAAABq8Q0AAAECAwQFBgcICQoLDA0OD9nM9elO5Bf169YlDVefyxQaKmUtjMEs_eSE7PO7DgdwKqrhByp39ReKIOpCIbfkDQ==";

    [Fact]
    public void FernetDecryptsPythonAndKeepsLegacyPlaintextAndEncryptedValues()
    {
        var secrets = Secrets(new() { ["Archive:EncryptionKey"] = Key });
        Assert.Equal("local-only-key", secrets.Decrypt(PythonToken));
        Assert.Equal(PythonToken, secrets.Encrypt(PythonToken));
        Assert.Equal("legacy-plain", secrets.Decrypt("legacy-plain"));
        Assert.Equal("", secrets.Encrypt(""));
        var encrypted = secrets.Encrypt("本地测试-key-xyz😀");
        Assert.StartsWith("fernet:", encrypted);
        Assert.EndsWith("=", encrypted);
        Assert.Equal("本地测试-key-xyz😀", secrets.Decrypt(encrypted));
        Assert.Equal("xyz😀", secrets.Tail(encrypted));
        Assert.NotEqual(encrypted, secrets.Encrypt("本地测试-key-xyz😀"));
    }

    [Fact]
    public void FernetRejectsTamperingWrongKeysAndMalformedValuesWithoutDisclosingSecrets()
    {
        var secrets = Secrets(new() { ["Archive:EncryptionKey"] = Key });
        var raw = LegacyToken.Decode(PythonToken[7..]);
        raw[^1] ^= 1;
        var changed = "fernet:" + Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_');
        Assert.Equal("无法解密归档 API key", Assert.Throws<CryptographicException>(() => secrets.Decrypt(changed)).Message);
        Assert.Equal("", secrets.Tail(changed));
        var wrongKey = Secrets(new() { ["Archive:EncryptionKey"] = Convert.ToBase64String(new byte[32]) });
        Assert.Throws<CryptographicException>(() => wrongKey.Decrypt(PythonToken));
        Assert.Equal("", secrets.Tail("fernet:invalid-base64"));
    }

    [Fact]
    public void FernetDerivesTheSameDefaultKeyAsPythonWithoutLoadingLegacyConfiguration()
    {
        var secrets = Secrets(new() { ["Auth:Secret"] = "local-encryption-test-secret-0123456789" });
        const string token = "fernet:gAAAAABq8Q0AAAECAwQFBgcICQoLDA0ODx3IC68WyPS9oCfGveQJi5GHwFU3ntyrPNrRf35MOkyaHncN7t1g3MXqk8GaGIsXIXTL_TYXVHL0qAT9pa_cyq0=";
        Assert.Equal("derived-local-key", secrets.Decrypt(token));
        Assert.Equal("-key", secrets.Tail(token));
        Assert.Equal("legacy", Secrets(new()).Decrypt("legacy"));
        Assert.Throws<InvalidOperationException>(() => Secrets(new()).Encrypt("cannot-store-unprotected"));
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://user:password@example.test")]
    [InlineData("https://example.test/?query=value")]
    [InlineData("https://example.test/#fragment")]
    [InlineData("https://localhost")]
    [InlineData("https://sub.localhost")]
    [InlineData("https://example.local")]
    [InlineData("https://example.internal")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://127.1")]
    [InlineData("https://2130706433")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://192.0.2.1")]
    [InlineData("https://224.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::ffff:127.0.0.1]")]
    [InlineData("https://[2001:db8::1]")]
    [InlineData("https://example.test\\@127.0.0.1")]
    public void ExternalApiUrlsRejectCredentialLeaksAndNonGlobalDestinations(string value)
        => Assert.Throws<ArgumentException>(() => ExternalApiUrl.Normalize(value, [], false));

    [Fact]
    public void ExternalApiUrlAllowlistIsRequiredAndMatchesOnlyDomainBoundaries()
    {
        Assert.Equal("https://api.example.test/v1", ExternalApiUrl.Normalize("  https://api.example.test/v1/// ", ["*.example.test"]));
        Assert.Equal("https://example.test", ExternalApiUrl.Normalize("https://example.test/", ["EXAMPLE.TEST."]));
        Assert.Throws<ArgumentException>(() => ExternalApiUrl.Normalize("https://evilexample.test", ["example.test"]));
        Assert.Throws<ArgumentException>(() => ExternalApiUrl.Normalize("https://example.test.attacker.test", ["example.test"]));
        Assert.Throws<ArgumentException>(() => ExternalApiUrl.Normalize("https://example.test", []));
        Assert.Equal("https://1.1.1.1", ExternalApiUrl.Normalize("https://1.1.1.1/", [], false));
        Assert.Equal("https://[2606:4700:4700::1111]", ExternalApiUrl.Normalize("https://[2606:4700:4700::1111]/", [], false));
    }

    [Fact]
    public async Task NameGateSharesCasefoldedNamesAndCancellationCannotLeakOrDoubleRelease()
    {
        var gate = new IdentityNameGate();
        var first = await gate.EnterAsync("user", "SameName", default);
        using var cancellation = new CancellationTokenSource();
        var waiting = gate.EnterAsync("user", "SAMENAME", cancellation.Token);
        Assert.False(waiting.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        first.Dispose();
        using var next = await gate.EnterAsync("user", "samename", default);
    }

    private static ArchiveSecrets Secrets(Dictionary<string, string?> values)
        => new(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), TimeProvider.System);
}
