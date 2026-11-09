using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Security;

public sealed class CaptchaService(MongoStore store, TimeProvider clock)
{
    public async Task<object> CreateAsync(CancellationToken ct)
    {
        var answer = RandomNumberGenerator.GetInt32(10000).ToString("D4");
        var info = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await store.Collection("csharp_captcha").InsertOneAsync(new BsonDocument
        {
            ["_id"] = info, ["digest"] = Digest(info, answer),
            ["expires_at"] = clock.GetUtcNow().UtcDateTime.AddMinutes(10)
        }, cancellationToken: ct);
        return new { info, image = "data:image/png;base64," + Convert.ToBase64String(CaptchaImage.Render(answer)) };
    }

    public async Task VerifyAsync(string info, string answer, CancellationToken ct)
    {
        // Consume on every attempt, atomically. Parallel guesses cannot reuse a challenge.
        var row = await store.Collection("csharp_captcha").FindOneAndDeleteAsync<BsonDocument>(
            new BsonDocument { ["_id"] = info, ["expires_at"] = new BsonDocument("$gt", clock.GetUtcNow().UtcDateTime) },
            cancellationToken: ct);
        if (row is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row["digest"].AsString),
                Convert.FromHexString(Digest(info, answer))))
            throw ApiError.Invalid("验证码无效或已过期");
    }

    internal static string Digest(string info, string answer) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(info + ":" + answer)));
}
