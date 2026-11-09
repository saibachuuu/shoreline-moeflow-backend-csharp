using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;

namespace Shoreline.Api.Infrastructure;

// Wiring is deliberately separate from Program.cs so the notification feature
// can be enabled without touching files owned by other workstreams.
public static class NotificationRegistration
{
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        services.AddScoped<NotificationContent>();
        services.AddScoped<NotificationReadService>();
        return services;
    }

    public static void MapNotificationEndpoints(this WebApplication app) =>
        NotificationEndpoints.MapNotifications(app);

    // Call after MongoStore.InitializeAsync during startup. Index names mirror
    // the legacy MongoEngine names; this is important because the collection is
    // shared with the Python service during migration.
    public static async Task InitializeAsync(MongoStore store, CancellationToken ct = default)
    {
        await EnsureAsync(store.Collection("notification"), ct,
            Index("key_1", true, false, "key"),
            Index("state_1_publish_at_-1", false, false, "state", "publish_at", -1),
            Index("category_1_created_at_-1", false, false, "category", "created_at", -1),
            Index("team_id_1_created_at_-1", false, false, "team_id", "created_at", -1),
            Index("project_id_1_created_at_-1", false, false, "project_id", "created_at", -1),
            Index("actor_id_1_created_at_-1", false, false, "actor_id", "created_at", -1),
            Index("search_tokens_1", false, false, "search_tokens"),
            Index("moderation_audited_1_revoked_at_-1", false, false, "moderation_audited", "revoked_at", -1));

        await EnsureAsync(store.Collection("notification_audience_chunk"), ct,
            Index("notification_id_1_number_1", true, false, "notification_id", "number"));

        await EnsureAsync(store.Collection("notification_receipt"), ct,
            Index("notification_id_1_user_id_1", true, false, "notification_id", "user_id"),
            Index("user_id_1__id_-1", false, false, "user_id", "_id", -1));

        await EnsureAsync(store.Collection("notification_delivery"), ct,
            Index("notification_id_1_user_id_1", true, false, "notification_id", "user_id"),
            Index("state_1_next_attempt_at_1", false, false, "state", "next_attempt_at"),
            Index("lease_until_1", false, false, "lease_until"));

        await EnsureAsync(store.Collection("notification_preference"), ct,
            Index("user_id_1", true, false, "user_id"));

        await EnsureAsync(store.Collection("notification_policy"), ct,
            Index("team_id_1", true, false, "team_id"));

        await EnsureAsync(store.Collection("notification_audit"), ct,
            Index("key_1", true, false, "key"),
            Index("notification_id_1_created_at_-1", false, false, "notification_id", "created_at", -1),
            Index("actor_id_1_created_at_-1", false, false, "actor_id", "created_at", -1));

        await EnsureAsync(store.Collection("notification_verified_email"), ct,
            Index("digest_1", true, false, "digest"));

        await EnsureAsync(store.Collection("notification_throttle"), ct,
            Index("key_1", true, false, "key"),
            Index("expires_at_1", false, false, TimeSpan.Zero, "expires_at"));
    }

    private static CreateIndexModel<BsonDocument> Index(
        string name, bool unique, bool sparse, params object[] keys)
    {
        var builder = Builders<BsonDocument>.IndexKeys;
        var fields = new List<(string Field, int Direction)>();
        foreach (var key in keys)
        {
            if (key is string field)
            {
                fields.Add((field, 1));
            }
            else if (key is int direction && fields.Count > 0)
            {
                fields[^1] = (fields[^1].Field, direction);
            }
        }
        var definitions = fields
            .Select(item => item.Direction < 0
                ? builder.Descending(item.Field)
                : builder.Ascending(item.Field))
            .ToArray();
        IndexKeysDefinition<BsonDocument> definition = definitions.Length == 1
            ? definitions[0]
            : builder.Combine(definitions);
        return new CreateIndexModel<BsonDocument>(definition,
            new CreateIndexOptions { Name = name, Unique = unique, Sparse = sparse });
    }

    private static CreateIndexModel<BsonDocument> Index(
        string name, bool unique, bool sparse, TimeSpan expireAfter, string field) =>
        new(Builders<BsonDocument>.IndexKeys.Ascending(field),
            new CreateIndexOptions { Name = name, Unique = unique, Sparse = sparse, ExpireAfter = expireAfter });

    private static async Task EnsureAsync(
        IMongoCollection<BsonDocument> collection,
        CancellationToken ct,
        params CreateIndexModel<BsonDocument>[] indexes)
    {
        try
        {
            await collection.Indexes.CreateManyAsync(indexes, ct);
        }
        catch (MongoCommandException error) when (error.Code == 85 || error.Code == 86)
        {
            // Same index name already exists with compatible keys. Re-creating
            // it would fail on some server versions; the legacy definition wins.
        }
    }
}
