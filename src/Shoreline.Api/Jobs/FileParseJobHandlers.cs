using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Shoreline.Api.Features;

namespace Shoreline.Api.Jobs;

/// <summary>
/// Celery <c>tasks.parse_text_task</c> equivalent. The worker is singleton and
/// the parse service is scoped, so every job gets a fresh DI scope and the
/// scoped label context is never captured.
/// </summary>
public sealed class ParseTextJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => FileParseService.ParseTextJobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FileParseService>()
            .RunParseTextAsync(payload, ct);
    }
}

/// <summary>Celery <c>tasks.find_terms_task</c> equivalent.</summary>
public sealed class FindTermsJobHandler(IServiceScopeFactory scopes) : IJobHandler
{
    public string Kind => FileParseService.FindTermsJobKind;

    public async Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FileParseService>()
            .RunFindTermsAsync(payload, ct);
    }
}
