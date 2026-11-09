using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Notifications;

namespace Shoreline.Api.Features;

public static class NotificationPublishRegistration
{
    public static IServiceCollection AddNotificationPublishServices(this IServiceCollection services)
    {
        services.AddScoped<NotificationPublishService>();
        services.AddSingleton<INotificationMailTransport, MailKitNotificationTransport>();
        services.AddHostedService<NotificationScanWorker>();
        return services;
    }

    public static WebApplication MapNotificationPublishEndpoints(this WebApplication app)
    {
        NotificationPublishEndpoints.MapNotificationPublish(app);
        return app;
    }

    public static async Task InitializeAsync(MongoStore store, CancellationToken ct)
    {
        await EnsureAsync(store.Collection("notification"),
            new BsonDocument("key", 1), "key_1", unique: true, ct);
        await EnsureAsync(store.Collection("notification_audience_chunk"),
            new BsonDocument { ["notification_id"] = 1, ["number"] = 1 },
            "notification_id_1_number_1", unique: true, ct);
        await EnsureAsync(store.Collection("notification_receipt"),
            new BsonDocument { ["notification_id"] = 1, ["user_id"] = 1 },
            "notification_id_1_user_id_1", unique: true, ct);
        await EnsureAsync(store.Collection("notification_delivery"),
            new BsonDocument { ["notification_id"] = 1, ["user_id"] = 1 },
            "notification_id_1_user_id_1", unique: true, ct);
        await EnsureAsync(store.Collection("notification_verified_email"),
            new BsonDocument("digest", 1), "digest_1", unique: true, ct);
        await EnsureAsync(store.Collection("notification_audit"),
            new BsonDocument("key", 1), "key_1", unique: true, ct);
    }

    private static async Task EnsureAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument keys,
        string name,
        bool unique,
        CancellationToken ct)
    {
        try
        {
            await collection.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Combine(
                        keys.Select(field => field.Value.AsInt32 > 0
                            ? Builders<BsonDocument>.IndexKeys.Ascending(field.Name)
                            : Builders<BsonDocument>.IndexKeys.Descending(field.Name)).ToArray()),
                    new CreateIndexOptions { Name = name, Unique = unique }),
                cancellationToken: ct);
        }
        catch (MongoCommandException error) when (error.Code is 85 or 86)
        {
            // An equivalent index already exists under another definition.
        }
    }
}
