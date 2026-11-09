using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Security;

public sealed class CurrentUser(MongoStore store, LegacyToken token)
{
    public async Task<BsonDocument> RequireAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Items.TryGetValue(typeof(CurrentUser), out var cached)) return (BsonDocument)cached!;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.Length == 0) throw ApiError.MissingToken();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)) throw ApiError.BadToken();
        var identity = token.Verify(authorization[7..]);
        if (!ObjectId.TryParse(identity.Id, out var id)) throw ApiError.BadToken();
        var user = await store.Collection("user").Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        if (user is null || identity.Characteristic != PasswordHash.Characteristic(user.Text("p"))) throw ApiError.BadToken();
        if (user.Flag("b")) throw new ApiError("UserBannedError", 1006, "此用户被封禁");
        context.Items[typeof(CurrentUser)] = user;
        return user;
    }

    public async Task<BsonDocument> RequireAdminAsync(HttpContext context, CancellationToken ct)
    {
        var user = await RequireAsync(context, ct);
        if (!user.Flag("admin")) throw ApiError.Forbidden();
        return user;
    }
}
