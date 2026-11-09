using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Api.Features;

public static class ProofreadFeedbackRegistration
{
    /// <summary>
    /// Registers the G3 service, the durable bake handler and a safe null
    /// renderer. Hosts that ship Shoreline.Imaging register the Magick-backed
    /// IProofreadImageRenderer after this call.
    /// </summary>
    public static IServiceCollection AddProofreadFeedbackServices(this IServiceCollection services)
    {
        services.AddScoped<ProofreadFeedbackService>();
        services.AddSingleton<IJobHandler, ProofreadDraftJobHandler>();
        services.TryAddSingleton<IProofreadImageRenderer, NullProofreadImageRenderer>();
        return services;
    }

    public static void MapProofreadFeedbackEndpoints(this WebApplication app) =>
        ProofreadFeedbackEndpoints.MapProofreadFeedback(app);

    /// <summary>
    /// Indexes used by the bake job and recipient lookup. The notification
    /// receipt/delivery indexes are owned by NotificationPublishRegistration;
    /// this method only adds the optional G3 job-kind lookup index.
    /// </summary>
    public static async Task InitializeAsync(MongoStore store, CancellationToken ct)
    {
        try
        {
            await store.Collection("csharp_job").Indexes.CreateOneAsync(
                new CreateIndexModel<MongoDB.Bson.BsonDocument>(
                    MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.IndexKeys
                        .Ascending("kind").Ascending("state").Ascending("available_at"),
                    new CreateIndexOptions { Name = "kind_1_state_1_available_at_1" }),
                cancellationToken: ct);
        }
        catch (MongoDB.Driver.MongoCommandException error) when (error.Code is 85 or 86)
        {
            // A concurrent process already created the index.
        }
    }
}
