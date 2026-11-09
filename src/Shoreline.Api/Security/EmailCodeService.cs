using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Security;

// The code and its durable mail job are one MongoDB document; no cross-document enqueue gap.
public sealed class EmailCodeService(MongoStore store, IConfiguration configuration, TimeProvider clock)
{
    public async Task<int> IssueAsync(int type, string email, CancellationToken ct)
    {
        if (!configuration.GetValue("Mail:Enabled", false)) throw new ApiError("MailUnavailableError", 2000, "邮件服务未启用", 503);
        var wait = Math.Max(1, configuration.GetValue($"Mail:WaitSeconds:{type}", 60));
        var now = clock.GetUtcNow().UtcDateTime;
        var key = $"{type}:{email}";
        var id = ObjectId.GenerateNewId();
        var verification = new BsonDocument
        {
            ["t"] = type, ["i"] = email, ["c"] = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6"),
            ["e"] = now.AddDays(2), ["s"] = now, ["w"] = 0, ["consumed"] = false
        };
        var collection = store.Collection("csharp_job");
        var existing = await collection.Find(new BsonDocument("code_key", key)).FirstOrDefaultAsync(ct);
        if (existing is not null) id = existing["_id"].AsObjectId;
        var filter = new BsonDocument
        {
            ["code_key"] = key, ["verification.s"] = new BsonDocument("$lte", now.AddSeconds(-wait)),
            ["state"] = new BsonDocument("$ne", "running")
        };
        try
        {
            await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update
                .SetOnInsert("_id", id).Set("kind", "security-email").Set("payload", new BsonDocument("code_job_id", id))
                .Set("code_key", key).Set("verification", verification).Set("state", "pending").Set("attempts", 0)
                .Set("available_at", now).Set("created_at", now).Set("purge_at", now.AddDays(7))
                .Unset("owner").Unset("lease_until").Unset("finished_at"), new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var current = await collection.Find(new BsonDocument("code_key", key)).FirstOrDefaultAsync(ct);
            var seconds = current is null ? wait : Math.Max(1, (int)Math.Ceiling(
                (current["verification"]["s"].ToUniversalTime().AddSeconds(wait) - now).TotalSeconds));
            throw new ApiError("VCodeCoolingError", 2004, "验证码冷却中")
            { ResponseMessage = new { wait = seconds, tip = $"请等候{seconds}秒后再试" } };
        }
        return wait;
    }

    public async Task VerifyAsync(int type, string email, string content, string field, bool consume, CancellationToken ct)
    {
        var collection = store.Collection("csharp_job");
        var row = await collection.Find(new BsonDocument("code_key", $"{type}:{email}")).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            await VerifyLegacyAsync(type, email, content, field, consume, ct);
            return;
        }
        var code = row["verification"].AsBsonDocument;
        if (code["consumed"].AsBoolean || code["w"].AsInt32 >= 5) throw ApiError.Field(field, "验证码失效，请重新获取");
        if (code["e"].ToUniversalTime() <= clock.GetUtcNow().UtcDateTime) throw ApiError.Field(field, "验证码过期，请重新输入");
        var filter = new BsonDocument
        {
            ["_id"] = row["_id"], ["verification.c"] = code["c"], ["verification.s"] = code["s"],
            ["verification.consumed"] = false, ["verification.w"] = new BsonDocument("$lt", 5),
            ["verification.e"] = new BsonDocument("$gt", clock.GetUtcNow().UtcDateTime)
        };
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(code["c"].AsString),
                System.Text.Encoding.UTF8.GetBytes(content)))
        {
            await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Inc("verification.w", 1), cancellationToken: ct);
            throw ApiError.Field(field, "验证码错误");
        }
        var result = await collection.UpdateOneAsync(filter,
            Builders<BsonDocument>.Update.Set("verification.consumed", consume), cancellationToken: ct);
        if (result.MatchedCount != 1) throw ApiError.Field(field, "验证码失效，请重新获取");
    }

    private async Task VerifyLegacyAsync(int type, string email, string content, string field, bool consume, CancellationToken ct)
    {
        var collection = store.Collection("v_code");
        var row = await collection.Find(new BsonDocument { ["t"] = type, ["i"] = email }).Sort(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
        if (row is null) throw ApiError.Field(field, "验证码失效，请重新获取");
        var filter = new BsonDocument { ["_id"] = row["_id"], ["c"] = row["c"], ["w"] = new BsonDocument("$lt", 5),
            ["e"] = new BsonDocument("$gt", clock.GetUtcNow().UtcDateTime) };
        if (row["e"].ToUniversalTime() <= clock.GetUtcNow().UtcDateTime) throw ApiError.Field(field, "验证码过期，请重新输入");
        if (row["c"].AsString != content)
        {
            await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Inc("w", 1), cancellationToken: ct);
            throw ApiError.Field(field, "验证码错误");
        }
        if (consume)
        {
            var result = await collection.DeleteOneAsync(filter, ct);
            if (result.DeletedCount != 1) throw ApiError.Field(field, "验证码失效，请重新获取");
        }
        else if (!await collection.Find(filter).AnyAsync(ct)) throw ApiError.Field(field, "验证码失效，请重新获取");
    }
}
