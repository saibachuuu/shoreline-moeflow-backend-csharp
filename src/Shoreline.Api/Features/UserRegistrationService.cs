using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

// Port of Python's RegisterSchema + UserAPI.post + User.create:
// field validation first (so a locally rejected request does not burn the
// one-time email code), then a single consumption of the type-2 code, the
// site whitelist, insertion under the shared user-name gate, and finally
// best-effort automatic team joins through site_setting.ajt.
//
// AdminUserListAPI.post reuses the same schema/User.create path without the
// email code or whitelist (app/validators/auth.py:167-184).
public sealed class UserRegistrationService(MongoStore store, SiteSettings settings, EmailCodeService codes,
    PasswordService passwords, LegacyToken tokens, IdentityNameGate names, TeamMemberService members,
    TimeProvider clock, ILogger<UserRegistrationService> logger)
{
    private const int ConfirmEmailCode = 2;

    // Same shapes as Python's EMAIL_REGEX / USER_NAME_REGEX (regexs/__init__.py),
    // anchored with \A..\z like the rest of the C# user services.
    private static readonly Regex EmailPattern = new(@"\A[^@ ]+@[^.@ ]+(\.[^.@ ]+)*(\.[^.@ ]{2,})\z", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(@"\A[\u2E80-\u2FDF\u3040-\u318F\u31A0-\u31BF\u31F0-\u31FF\u3400-\u4DB5\u4E00-\u9FFF\uA960-\uA97F\uAC00-\uD7FFa-zA-Z0-9_]+\z", RegexOptions.Compiled);

    private sealed record AccountInput(string Email, string EmailLower, string Name, string Password, string? VCode);

    public async Task<(BsonDocument User, string Token)> RegisterAsync(JsonElement data, string requestId, CancellationToken ct)
    {
        var input = await ValidateAccountAsync(data, requireCode: true, ct);

        // Hash before consuming the code: the bounded hasher can still reject
        // with 429, and that must not burn the caller's one-time code.
        var hash = await passwords.CreateAsync(input.Password, ct);
        await codes.VerifyAsync(ConfirmEmailCode, input.EmailLower, input.VCode!, "v_code", true, ct);

        // Python checks the whitelist after schema validation (i.e. after the
        // code is consumed); keep that order for compatibility. A rejected
        // registration must therefore request a fresh confirmation code.
        var setting = await settings.GetAsync(ct);
        if (setting.GetValue("ew", true).AsBoolean)
        {
            var whitelist = setting.GetValue("we", new BsonArray()).AsBsonArray
                .Where(v => v.IsString).Select(v => v.AsString).ToHashSet();
            if (!whitelist.Contains(input.EmailLower) && !whitelist.Contains(input.Email))
                throw new ApiError("EmailNotInWhitelistError", 1012, "邮箱不在白名单中，请联系管理员添加");
        }

        var user = await InsertUserAsync(input.EmailLower, input.Name, hash, ct);
        await AutoJoinAsync(user, setting, requestId, ct);
        return (user, tokens.Issue(user["_id"].ToString()!, hash));
    }

    /// <summary>
    /// Python AdminUserListAPI.post + AdminRegisterSchema: an administrator
    /// creates an ordinary account. There is no email code, no whitelist and no
    /// returned token; User.create still applies automatic team joins.
    /// </summary>
    public async Task<BsonDocument> AdminCreateAsync(JsonElement data, string requestId, CancellationToken ct)
    {
        var input = await ValidateAccountAsync(data, requireCode: false, ct);
        var hash = await passwords.CreateAsync(input.Password, ct);
        var setting = await settings.GetAsync(ct);
        var user = await InsertUserAsync(input.EmailLower, input.Name, hash, ct);
        await AutoJoinAsync(user, setting, requestId, ct);
        return user;
    }

    private async Task<AccountInput> ValidateAccountAsync(JsonElement data, bool requireCode, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message)
        {
            errors[field] = errors.TryGetValue(field, out var existing) ? [.. existing, message] : [message];
        }
        string? Text(string field, int maxLength)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            {
                Add(field, "Missing data for required field.");
                return null;
            }
            var text = value.GetString()!;
            if (text.EnumerateRunes().Count() > maxLength)
            {
                Add(field, "内容过长");
                return null;
            }
            return text;
        }

        var email = Text("email", 254);
        var name = Text("name", 1024);
        var password = Text("password", 1024);
        var vCode = requireCode ? Text("v_code", 128) : null;
        if (password is not null && (password.EnumerateRunes().Count() < 6 || password.EnumerateRunes().Count() > 60)) Add("password", "长度为6到60个字符");
        if (requireCode && vCode is not null && vCode.Length == 0) Add("v_code", "验证码不能为空");

        string? emailLower = null;
        if (email is not null)
        {
            if (!MailAddress.TryCreate(email, out var address) || address.Address != email || !address.Host.Contains('.') || !EmailPattern.IsMatch(email))
                Add("email", "邮箱格式不正确");
            else
            {
                emailLower = email.ToLowerInvariant();
                if (await store.Collection("user").Find(new BsonDocument("e", emailLower)).AnyAsync(ct))
                    Add("email", "此邮箱已被注册");
            }
        }
        if (name is not null)
        {
            var length = name.EnumerateRunes().Count();
            if (length < 2 || length > 18) Add("name", "长度为2到18个字符");
            else if (!NamePattern.IsMatch(name)) Add("name", "仅可使用中文/日文/韩文/英文/数字/_");
            else if (await NameTakenAsync(name, ct)) Add("name", "此昵称已被使用");
        }
        if (errors.Count > 0) throw Validation(errors);

        return new AccountInput(email!, emailLower!, name!, password!, vCode);
    }

    private async Task<BsonDocument> InsertUserAsync(string emailLower, string name, string hash, CancellationToken ct)
    {
        BsonDocument user;
        using (await names.EnterAsync("user", name, ct))
        {
            var users = store.Collection("user");
            if (await users.Find(new BsonDocument("e", emailLower)).AnyAsync(ct)) throw ApiError.Field("email", "此邮箱已被注册");
            if (await NameTakenAsync(name, ct)) throw ApiError.Field("name", "此昵称已被使用");
            var now = clock.GetUtcNow().UtcDateTime;
            user = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["e"] = emailLower, ["n"] = name,
                ["as"] = new BsonArray(), ["ns"] = SearchText.Normalize(name), ["asrch"] = new BsonArray(),
                ["ddn"] = "", ["s"] = "", ["l"] = "auto", ["t"] = "", ["a"] = "",
                ["b"] = false, ["p"] = hash, ["admin"] = false, ["c"] = now
            };
            try
            {
                await users.InsertOneAsync(user, cancellationToken: ct);
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Lost a cross-process race past the pre-checks (the name gate
                // only serializes this process); report the colliding field.
                if (await users.Find(new BsonDocument("e", emailLower)).AnyAsync(ct)) throw ApiError.Field("email", "此邮箱已被注册");
                if (await NameTakenAsync(name, ct)) throw ApiError.Field("name", "此昵称已被使用");
                throw ApiError.Invalid("用户信息已变化，请重试");
            }
        }
        return user;
    }

    private async Task AutoJoinAsync(BsonDocument user, BsonDocument setting, string requestId, CancellationToken ct)
    {
        // Python's User.create persists the user before joining, so a failed
        // join leaves a working account that can simply log in. Keep the
        // account, attempt every remaining team, and never fail a completed
        // registration because of auto-join configuration.
        foreach (var teamId in AutoJoinIds(setting))
        {
            var team = await store.Collection("team").Find(new BsonDocument("_id", teamId)).FirstOrDefaultAsync(ct);
            if (team is null) continue; // Python silently skips missing teams.
            try
            {
                await members.JoinTrustedAsync(team, user, await DefaultBaseTagAsync(team, ct), requestId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Automatic team join failed for new user {UserId} and team {TeamId}", user["_id"], teamId);
            }
        }
    }

    private async Task<bool> NameTakenAsync(string name, CancellationToken ct) =>
        await store.Collection("user").Find(new BsonDocument("n", new BsonRegularExpression("^" + Regex.Escape(name) + "$", "i"))).AnyAsync(ct);

    private async Task<string> DefaultBaseTagAsync(BsonDocument team, CancellationToken ct)
    {
        // Python's join_team maps team.default_role.system_code through
        // {creator, admin, member} and falls back to "member" (the default
        // system role is "beginner", so new members normally land as member).
        if (team.TryGetValue("dr", out var roleId) && roleId.IsObjectId)
        {
            var role = await store.Collection("team_role").Find(new BsonDocument("_id", roleId.AsObjectId)).FirstOrDefaultAsync(ct);
            var code = role?.Text("m_o");
            if (code is "creator" or "admin" or "member") return code;
        }
        return "member";
    }

    private static IEnumerable<ObjectId> AutoJoinIds(BsonDocument setting)
    {
        if (!setting.TryGetValue("ajt", out var value) || !value.IsBsonArray) yield break;
        foreach (var item in value.AsBsonArray)
        {
            if (item.IsObjectId) yield return item.AsObjectId;
            else if (ObjectId.TryParse(item.ToString(), out var parsed)) yield return parsed;
        }
    }

    private static ApiError Validation(Dictionary<string, string[]> errors) =>
        new("ValidateError", 2, "Validate Error") { ResponseMessage = errors };
}
