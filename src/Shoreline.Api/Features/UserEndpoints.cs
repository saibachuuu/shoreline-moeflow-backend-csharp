using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

public static class UserEndpoints
{
    public static void MapUsers(this WebApplication app)
    {
        app.MapPost("/v1/captchas", (CaptchaService captcha, CancellationToken ct) => captcha.CreateAsync(ct));
        app.MapPost("/v1/user/token", async (JsonElement data, MongoStore store, CaptchaService captcha, PasswordService passwords, LegacyToken token, CancellationToken ct) =>
        {
            var email = data.RequiredText("email", 320).ToLowerInvariant();
            var password = data.Password("password");
            var info = data.RequiredText("captcha_info", 128);
            var answer = data.RequiredText("captcha", 32);
            await captcha.VerifyAsync(info, answer, ct);
            var user = await store.Collection("user").Find(new BsonDocument("e", email)).FirstOrDefaultAsync(ct)
                ?? throw new ApiError("EmailNotRegisteredError", 1001, "此邮箱未注册");
            if (!await passwords.VerifyAsync(user.Text("p"), password, ct)) throw new ApiError("PasswordWrongError", 1002, "密码错误");
            if (user.Flag("b")) throw new ApiError("UserBannedError", 1006, "此用户被封禁");
            return new { token = token.Issue(user["_id"].ToString()!, user.Text("p")) };
        }).RequireRateLimiting("auth");

        app.MapPost("/v1/users", async (JsonElement data, HttpContext ctx, UserRegistrationService registrations, CancellationToken ct) =>
        {
            var (_, token) = await registrations.RegisterAsync(data, RequestId(ctx), ct);
            return new { message = "注册成功", token };
        });

        app.MapGet("/v1/user/info", async (HttpContext ctx, CurrentUser current, UserView view, CancellationToken ct) =>
            await view.ToApiAsync(await current.RequireAsync(ctx, ct), ctx));
        app.MapGet("/v1/users/{name}", async (string name, MongoStore store, UserView view, HttpContext ctx, CancellationToken ct) =>
        {
            var user = await store.Collection("user").Find(new BsonDocument("n", name)).FirstOrDefaultAsync(ct)
                ?? throw new ApiError("UserNotExistError", 1005, "用户不存在");
            // Public endpoint deliberately does not authenticate an optional bearer, matching Flask.
            return await view.ToApiAsync(user, ctx);
        });
        app.MapGet("/v1/users", async (HttpContext ctx, CurrentUser current, MongoStore store, UserView view, CancellationToken ct) =>
        {
            await current.RequireAsync(ctx, ct);
            var word = ctx.Request.Query["word"].ToString();
            if (word.Length == 0 || word.Length > 256) throw ApiError.Invalid("word: 搜索词不可为空");
            var page = Pagination.From(ctx.Request);
            var pattern = new BsonRegularExpression(Regex.Escape(SearchText.Normalize(word)), "i");
            var filter = new BsonDocument("$or", new BsonArray { new BsonDocument("ns", pattern), new BsonDocument("asrch", pattern) });
            var users = store.Collection("user");
            var count = await users.CountDocumentsAsync(filter, cancellationToken: ct);
            var rows = await users.Find(filter).Skip(page.Skip).Limit(page.Limit).ToListAsync(ct);
            page.Apply(ctx.Response, count);
            return await Task.WhenAll(rows.Select(row => view.ToApiAsync(row, ctx)));
        });
        app.MapPut("/v1/user/password", async (JsonElement data, HttpContext ctx, CurrentUser current, MongoStore store, PasswordService passwords, CancellationToken ct) =>
        {
            var user = await current.RequireAsync(ctx, ct);
            var oldPassword = data.Password("old_password");
            var newPassword = data.Password("new_password");
            if (!await passwords.VerifyAsync(user.Text("p"), oldPassword, ct)) throw new ApiError("PasswordWrongError", 1002, "密码错误");
            var hash = await passwords.CreateAsync(newPassword, ct);
            var result = await store.Collection("user").UpdateOneAsync(new BsonDocument { ["_id"] = user["_id"], ["p"] = user["p"] },
                Builders<BsonDocument>.Update.Set("p", hash), cancellationToken: ct);
            if (result.MatchedCount != 1) throw ApiError.BadToken();
            return new { message = "修改成功，请重新登陆" };
        }).RequireRateLimiting("auth");

        app.MapPut("/v1/user/info", async (JsonElement data, HttpContext ctx, CurrentUser current, UserProfileService profiles, UserView view, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(ctx, ct);
            var (user, _) = await profiles.UpdateAsync(actor, actor, data, false, RequestId(ctx), ct);
            return new { message = "修改成功", user = await view.ToApiAsync(user, ctx) };
        });
        app.MapPatch("/v1/me/aliases", async (JsonElement data, HttpContext ctx, CurrentUser current, UserProfileService profiles, UserView view, CancellationToken ct) =>
        {
            var actor = await current.RequireAsync(ctx, ct);
            var (user, auditId) = await profiles.UpdateAsync(actor, actor, data, true, RequestId(ctx), ct);
            return new { user = await view.ToApiAsync(user, ctx), audit_event_id = auditId.ToString() };
        });
        app.MapPatch("/v1/users/{id}/aliases", async (string id, JsonElement data, HttpContext ctx, CurrentUser current,
            MongoStore store, UserProfileService profiles, UserView view, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(ctx, ct);
            var target = await store.Collection("user").Find(new BsonDocument("_id", BsonFields.Id(id))).FirstOrDefaultAsync(ct)
                ?? throw new ApiError("UserNotExistError", 1005, "用户不存在");
            var (user, auditId) = await profiles.UpdateAsync(target, actor, data, true, RequestId(ctx), ct);
            return new { user = await view.ToApiAsync(user, ctx), audit_event_id = auditId.ToString() };
        });
    }

    private static string RequestId(HttpContext ctx) => ctx.Request.Headers["X-Request-ID"].FirstOrDefault()
        ?? ctx.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
}

public sealed class UserView(IConfiguration configuration, IServiceProvider services)
{
    public async Task<Dictionary<string, object?>> ToApiAsync(BsonDocument user, HttpContext ctx)
    {
        var locale = user.Text("l", "auto");
        var chinese = LanguageCatalog.ChinesePreferred(ctx.Request.GetTypedHeaders().AcceptLanguage);
        var name = locale switch
        {
            "zh_CN" => chinese ? "中文（简体）" : "Chinese (Simplified)",
            "zh_TW" => chinese ? "中文（繁体）" : "Chinese (Traditional)",
            "en" => chinese ? "英文" : "English",
            _ => chinese ? "自动" : "Auto"
        };
        var avatar = user.Text("a");
        var result = new Dictionary<string, object?>
        {
            ["id"] = user["_id"].ToString(), ["name"] = user.Text("n"), ["default_display_name"] = user.Text("ddn"),
            ["signature"] = user.Text("s"), ["has_avatar"] = avatar.Length > 0,
            ["avatar"] = avatar.Length == 0 ? configuration["Storage:DefaultUserAvatar"] :
                await services.GetRequiredService<ObjectStorage>().SignGetAsync(AvatarStoragePrefixes.User(configuration) + avatar, ObjectStorage.DefaultSignedUrlValidity),
            ["locale"] = new { id = locale, name, intro = locale == "auto" ? (chinese ? "遵循浏览器设置" : "Follow browser settings") : "" },
            ["admin"] = user.Flag("admin"),
            ["aliases"] = user.GetValue("as", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray()
        };
        if (ctx.Items.TryGetValue(typeof(CurrentUser), out var value) && value is BsonDocument viewer)
        {
            if (viewer.Flag("admin")) result["email"] = user.Text("e");
            if (viewer["_id"] == user["_id"])
                result["can_manage_site_admins"] = viewer.Flag("admin") && !viewer.Flag("b") &&
                    string.Equals(viewer.Text("e"), configuration["Auth:AdminEmail"], StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }
}
