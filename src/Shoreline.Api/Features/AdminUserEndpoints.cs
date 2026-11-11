using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

public static class AdminUserEndpoints
{
    public static void MapAdminUsers(this WebApplication app)
    {
        app.MapGet("/v1/admin/users", async (HttpContext ctx, CurrentUser current, MongoStore store, UserView view, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(ctx, ct);
            // Deliberate C-class deviation: Python app/apis/user.py:161 does
            // `word = query["word"]` and raises KeyError -> HTTP 500 when the
            // parameter is absent. Accepting an empty word is a superset and
            // does not change any successful legacy response.
            var word = ctx.Request.Query["word"].ToString();
            if (word.Length > 256) throw ApiError.Field("word", "内容过长");
            var page = Pagination.From(ctx.Request);
            var pattern = new BsonRegularExpression(Regex.Escape(SearchText.Normalize(word)), "i");
            var filter = word.Length == 0 ? new BsonDocument() : new BsonDocument("$or", new BsonArray
                { new BsonDocument("ns", pattern), new BsonDocument("asrch", pattern) });
            var users = store.Collection("user");
            var count = await users.CountDocumentsAsync(filter, cancellationToken: ct);
            var rows = await users.Find(filter).Sort(new BsonDocument { ["c"] = -1, ["_id"] = -1 })
                .Skip(page.Skip).Limit(page.Limit).ToListAsync(ct);
            page.Apply(ctx.Response, count);
            return await Task.WhenAll(rows.Select(user => view.ToApiAsync(user, ctx)));
        });
        // Python AdminUserListAPI.post (app/apis/user.py:169-176): admin-only
        // account creation through AdminRegisterSchema + User.create. Unlike
        // public registration there is no email code, no whitelist and no
        // token in the response.
        app.MapPost("/v1/admin/users", async (JsonElement data, HttpContext ctx, CurrentUser current,
            UserRegistrationService users, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(ctx, ct);
            var requestId = ctx.Request.Headers["X-Request-ID"].FirstOrDefault()
                ?? ctx.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
            await users.AdminCreateAsync(data, requestId, ct);
            return new { message = LegacySuccessMessages.Get(ctx, "注册成功") };
        });
        app.MapPut("/v1/admin/users/{id}", async (string id, JsonElement data, HttpContext ctx, CurrentUser current,
            MongoStore store, PasswordService passwords, CancellationToken ct) =>
        {
            await current.RequireAdminAsync(ctx, ct);
            var filter = new BsonDocument("_id", BsonFields.Id(id));
            if (!await store.Collection("user").Find(filter).AnyAsync(ct)) throw MissingUser();
            var hash = await passwords.CreateAsync(data.Password("password"), ct);
            var result = await store.Collection("user").UpdateOneAsync(filter,
                Builders<BsonDocument>.Update.Set("p", hash), cancellationToken: ct);
            if (result.MatchedCount == 0) throw MissingUser();
            return new { message = LegacySuccessMessages.Get(ctx, "修改成功") };
        }).RequireRateLimiting("auth");
        app.MapPut("/v1/admin/admin-status", async (JsonElement data, HttpContext ctx, CurrentUser current,
            MongoStore store, IConfiguration configuration, CancellationToken ct) =>
        {
            var actor = await current.RequireAdminAsync(ctx, ct);
            if (!string.Equals(actor.Text("e"), configuration["Auth:AdminEmail"], StringComparison.OrdinalIgnoreCase))
                throw ApiError.Forbidden();
            var id = BsonFields.Id(data.RequiredText("user_id", 24));
            var status = data.RequiredBoolean("status");
            var result = await store.Collection("user").UpdateOneAsync(new BsonDocument("_id", id),
                Builders<BsonDocument>.Update.Set("admin", status), cancellationToken: ct);
            if (result.MatchedCount == 0) throw MissingUser();
            return new { message = LegacySuccessMessages.Get(ctx, "修改成功") };
        });
    }

    private static ApiError MissingUser() => new("UserNotExistError", 1005, "用户不存在");
}
