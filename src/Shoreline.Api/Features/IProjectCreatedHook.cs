using MongoDB.Bson;

namespace Shoreline.Api.Features;

public interface IProjectCreatedHook
{
    Task OnProjectCreatedAsync(ObjectId projectId, CancellationToken ct);
}
