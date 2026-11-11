using System.Net.Mail;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class EmailEndpoints
{
    public static void MapEmail(this WebApplication app)
    {
        app.MapPost("/v1/confirm-email-codes", async (JsonElement data, MongoStore store, CaptchaService captcha, EmailCodeService codes, CancellationToken ct) =>
        {
            var email = Email(data, "email");
            if (await store.Collection("user").Find(new BsonDocument("e", email)).AnyAsync(ct)) throw ApiError.Field("email", "此邮箱已注册");
            await captcha.VerifyAsync(data.RequiredText("captcha_info"), data.RequiredText("captcha"), ct);
            return new { wait = await codes.IssueAsync(2, email, ct) };
        }).RequireRateLimiting("auth");
        app.MapPost("/v1/reset-email-codes", async (HttpContext ctx, CurrentUser current, EmailCodeService codes, CancellationToken ct) =>
        {
            var user = await current.RequireAsync(ctx, ct);
            return new { wait = await codes.IssueAsync(3, user.Text("e"), ct) };
        }).RequireRateLimiting("auth");
        app.MapPost("/v1/reset-password-codes", async (JsonElement data, MongoStore store, CaptchaService captcha, EmailCodeService codes, CancellationToken ct) =>
        {
            var email = Email(data, "email");
            if (!await store.Collection("user").Find(new BsonDocument("e", email)).AnyAsync(ct)) throw ApiError.Field("email", "此邮箱未注册");
            await captcha.VerifyAsync(data.RequiredText("captcha_info"), data.RequiredText("captcha"), ct);
            return new { wait = await codes.IssueAsync(4, email, ct) };
        }).RequireRateLimiting("auth");
        app.MapDelete("/v1/user/password", async ([Microsoft.AspNetCore.Mvc.FromBody] JsonElement data, MongoStore store, EmailCodeService codes, PasswordService passwords, LegacyToken tokens, CancellationToken ct) =>
        {
            var email = Email(data, "email");
            var password = data.Password("password");
            var code = data.RequiredText("v_code", 128);
            var user = await store.Collection("user").Find(new BsonDocument("e", email)).FirstOrDefaultAsync(ct)
                ?? throw ApiError.Field("email", "此邮箱未注册");
            var hash = await passwords.CreateAsync(password, ct);
            await codes.VerifyAsync(4, email, code, "v_code", true, ct);
            var filter = new BsonDocument { ["_id"] = user["_id"], ["e"] = email, ["p"] = user["p"] };
            var updated = await store.Collection("user").FindOneAndUpdateAsync<BsonDocument>(filter,
                Builders<BsonDocument>.Update.Set("p", hash), new() { ReturnDocument = ReturnDocument.After }, ct);
            if (updated is null) throw ApiError.Invalid("用户信息已变化，请重试");
            return new { token = tokens.Issue(user["_id"].ToString()!, hash) };
        }).RequireRateLimiting("auth");
        app.MapPut("/v1/user/email", async (JsonElement data, HttpContext ctx, CurrentUser current, MongoStore store,
            EmailCodeService codes, UserView view, CancellationToken ct) =>
        {
            var user = await current.RequireAsync(ctx, ct);
            var email = Email(data, "new_email");
            var oldCode = data.RequiredText("old_email_v_code", 128);
            var newCode = data.RequiredText("new_email_v_code", 128);
            if (await store.Collection("user").Find(new BsonDocument("e", email)).AnyAsync(ct)) throw ApiError.Field("new_email", "此邮箱已注册");
            await codes.VerifyAsync(3, user.Text("e"), oldCode, "old_email_v_code", false, ct);
            await codes.VerifyAsync(2, email, newCode, "new_email_v_code", false, ct);
            var updated = await store.Collection("user").FindOneAndUpdateAsync<BsonDocument>(
                new BsonDocument { ["_id"] = user["_id"], ["e"] = user["e"] }, Builders<BsonDocument>.Update.Set("e", email),
                new() { ReturnDocument = ReturnDocument.After }, ct);
            if (updated is null) throw ApiError.Invalid("用户信息已变化，请重试");
            return new { message = LegacySuccessMessages.Get(ctx, "修改成功"), user = await view.ToApiAsync(updated, ctx) };
        }).RequireRateLimiting("auth");
    }

    private static string Email(JsonElement data, string field)
    {
        var value = data.RequiredText(field, 254);
        if (!MailAddress.TryCreate(value, out var address) || address.Address != value || !address.Host.Contains('.'))
            throw ApiError.Field(field, "邮箱地址无效");
        return value.ToLowerInvariant();
    }
}
