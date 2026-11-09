using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Security;

// itsdangerous.URLSafeTimedSerializer plus the historical HS512 JWS format.
public sealed class LegacyToken
{
    private readonly byte[] secret;
    private readonly byte[] signingKey;
    private readonly TimeProvider clock;
    public LegacyToken(IConfiguration configuration, TimeProvider clock)
    {
        var key = configuration["Auth:Secret"];
        if (string.IsNullOrEmpty(key) || key.Length < 32)
            throw new InvalidOperationException("Auth:Secret must contain at least 32 characters");
        secret = Encoding.UTF8.GetBytes(key);
        signingKey = SHA1.HashData([.. Encoding.UTF8.GetBytes("itsdangerous"), .. Encoding.UTF8.GetBytes("signer"), .. secret]);
        this.clock = clock;
    }

    public string Issue(string id, string passwordHash)
    {
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { id, pc = PasswordHash.Characteristic(passwordHash) }));
        Span<byte> timestamp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(timestamp, clock.GetUtcNow().ToUnixTimeSeconds());
        var start = 0;
        while (start < 7 && timestamp[start] == 0) start++;
        var value = payload + "." + Encode(timestamp[start..]);
        return value + "." + Encode(HMACSHA1.HashData(signingKey, Encoding.ASCII.GetBytes(value)));
    }

    public (string Id, string Characteristic) Verify(string token)
    {
        try
        {
            if (token.Length > 8192) throw ApiError.BadToken();
            var last = token.LastIndexOf('.');
            if (last < 0) throw ApiError.BadToken();
            var signed = token[..last];
            var signature = Decode(token[(last + 1)..]);
            byte[] payload;
            if (CryptographicOperations.FixedTimeEquals(signature, HMACSHA1.HashData(signingKey, Encoding.ASCII.GetBytes(signed))))
            {
                var separator = signed.LastIndexOf('.');
                if (separator < 0) throw ApiError.BadToken();
                var stamp = Decode(signed[(separator + 1)..]);
                if (stamp.Length > 8) throw ApiError.BadToken();
                long issued = 0;
                foreach (var b in stamp) issued = checked(issued * 256 + b);
                var age = clock.GetUtcNow().ToUnixTimeSeconds() - issued;
                if (age is < 0 or > 2_592_000) throw ApiError.BadToken();
                var data = signed[..separator];
                payload = Decode(data.TrimStart('.'));
                if (data.StartsWith('.'))
                {
                    using var input = new MemoryStream(payload);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    var buffer = new byte[1024];
                    int read;
                    while ((read = zlib.Read(buffer)) > 0)
                    {
                        if (output.Length + read > 8192) throw ApiError.BadToken();
                        output.Write(buffer, 0, read);
                    }
                    payload = output.ToArray();
                }
            }
            else
            {
                var parts = token.Split('.');
                if (parts.Length != 3 || !CryptographicOperations.FixedTimeEquals(signature,
                    HMACSHA512.HashData(secret, Encoding.ASCII.GetBytes(signed)))) throw ApiError.BadToken();
                using var header = JsonDocument.Parse(Decode(parts[0]));
                if (header.RootElement.GetProperty("alg").GetString() != "HS512" ||
                    header.RootElement.GetProperty("exp").GetInt64() < clock.GetUtcNow().ToUnixTimeSeconds())
                    throw ApiError.BadToken();
                payload = Decode(parts[1]);
            }
            using var json = JsonDocument.Parse(payload);
            return (json.RootElement.GetProperty("id").GetString() ?? "", json.RootElement.GetProperty("pc").GetString() ?? "");
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidDataException or KeyNotFoundException
            or InvalidOperationException or OverflowException)
        {
            throw ApiError.BadToken();
        }
    }

    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}
