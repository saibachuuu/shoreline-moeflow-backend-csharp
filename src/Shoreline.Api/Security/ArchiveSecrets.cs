using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shoreline.Api.Security;

// Python cryptography.fernet compatibility; no native helper or child process.
public sealed class ArchiveSecrets(IConfiguration configuration, TimeProvider clock)
{
    private const string Prefix = "fernet:";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Lazy<byte[]> key = new(() => ResolveKey(configuration));

    public string Encrypt(string value)
    {
        if (value.Length == 0 || value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var plaintext = Utf8.GetBytes(value);
        try
        {
            using var aes = Aes.Create();
            aes.Key = key.Value[16..];
            var iv = RandomNumberGenerator.GetBytes(16);
            var ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
            var token = new byte[25 + ciphertext.Length + 32];
            token[0] = 0x80;
            BinaryPrimitives.WriteInt64BigEndian(token.AsSpan(1, 8), clock.GetUtcNow().ToUnixTimeSeconds());
            iv.CopyTo(token, 9);
            ciphertext.CopyTo(token, 25);
            HMACSHA256.HashData(key.Value.AsSpan(0, 16), token.AsSpan(0, token.Length - 32)).CopyTo(token, token.Length - 32);
            // Fernet uses padded URL-safe base64, unlike itsdangerous tokens.
            return Prefix + Convert.ToBase64String(token).Replace('+', '-').Replace('/', '_');
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string Decrypt(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        try
        {
            if (value.Length > 65536) throw new CryptographicException();
            var token = LegacyToken.Decode(value[Prefix.Length..]);
            if (token.Length < 73 || token[0] != 0x80 || (token.Length - 57) % 16 != 0) throw new CryptographicException();
            var signed = token.AsSpan(0, token.Length - 32);
            if (!CryptographicOperations.FixedTimeEquals(token.AsSpan(token.Length - 32), HMACSHA256.HashData(key.Value.AsSpan(0, 16), signed)))
                throw new CryptographicException();
            using var aes = Aes.Create();
            aes.Key = key.Value[16..];
            var plaintext = aes.DecryptCbc(token.AsSpan(25, token.Length - 57), token.AsSpan(9, 16), PaddingMode.PKCS7);
            try { return Utf8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception error) when (error is FormatException or CryptographicException or DecoderFallbackException)
        {
            throw new CryptographicException("无法解密归档 API key");
        }
    }

    public string Tail(string value, int length = 4)
    {
        try
        {
            var runes = Decrypt(value).EnumerateRunes().ToArray();
            return string.Concat(runes.Skip(Math.Max(0, runes.Length - length)).Select(rune => rune.ToString()));
        }
        catch (CryptographicException) { return ""; }
    }

    private static byte[] ResolveKey(IConfiguration configuration)
    {
        var configured = configuration["Archive:EncryptionKey"]?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            var decoded = LegacyToken.Decode(configured);
            return decoded.Length == 32 ? decoded : throw new CryptographicException("Archive:EncryptionKey must decode to 32 bytes");
        }
        var secret = configuration["Auth:Secret"];
        if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("Auth:Secret is required to protect archive API keys");
        return SHA256.HashData(Utf8.GetBytes(secret));
    }
}
