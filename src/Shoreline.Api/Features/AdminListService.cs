using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

// Legacy-compatible GET /v1/admin/v-codes (app/apis/v_code.py:135-153).
// The legacy admin list reads the shared `v_code` collection (mongoengine
// inheritance), NOT the newer email-code source and NOT the captcha collection.
public sealed class AdminListService(MongoStore store)
{
    private static readonly Dictionary<int, (string Zh, string En)> VCodeIntros = new()
    {
        [1] = ("人机验证码", "Human verification code"),
        [2] = ("验证邮箱", "Verify email"),
        [3] = ("重设邮箱", "Reset email"),
        [4] = ("重置密码", "Reset password"),
        [5] = ("验证手机", "Verify phone"),
        [6] = ("重设手机", "Reset phone")
    };

    public async Task<object[]> VCodesAsync(bool chinese, CancellationToken ct = default)
    {
        // `type__ne=CAPTCHA` then limit(100) then order_by("-send_time"),
        // exactly matching the legacy orchestration order from the public API.
        var rows = await store.Collection("v_code")
            .Find(new BsonDocument("t", new BsonDocument("$ne", 1)))
            .Sort(new BsonDocument("s", -1))
            .Limit(100)
            .ToListAsync(ct);
        return rows.Select(row => (object)new Dictionary<string, object?>
        {
            ["id"] = row["_id"].ToString(),
            ["content"] = row.Text("c"),
            ["intro"] = VCodeIntros.TryGetValue((int)row.Integer("t"), out var intro)
                ? (chinese ? intro.Zh : intro.En) : "未知类型",
            ["info"] = row.Text("i"),
            ["expires"] = IsoTimestamp(row.GetValue("e", BsonNull.Value)),
            ["wrong_count"] = row.Integer("w"),
            ["send_time"] = IsoTimestamp(row.GetValue("s", BsonNull.Value))
        }).ToArray();
    }

    // Python datetime.isoformat(): UTC, no trailing Z, six fractional digits
    // when microseconds are non-zero and no fraction at all when they are zero.
    public static string? IsoTimestamp(BsonValue value)
    {
        if (!value.IsBsonDateTime) return null;
        var utc = value.ToUniversalTime();
        var second = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var microseconds = (int)(utc.Ticks % TimeSpan.TicksPerSecond / 10);
        return microseconds == 0 ? second : second + "." + microseconds.ToString("D6", CultureInfo.InvariantCulture);
    }
}
