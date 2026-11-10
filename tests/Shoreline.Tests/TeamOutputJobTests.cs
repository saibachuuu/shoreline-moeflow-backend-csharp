using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[CollectionDefinition("local-services-team-output")]
public sealed class TeamOutputServicesCollection : ICollectionFixture<LocalServicesWithoutWorker>;

/// <summary>
/// Python exposes the team export as the Celery task
/// <c>tasks.output_team_projects_task</c>; the C# port had only the direct
/// <see cref="OutputService.CreateTeamAsync"/> body. These facts cover the
/// missing durable kind, its handler body and the run_sync/direct parity.
/// </summary>
[Collection("local-services-team-output")]
public sealed class TeamOutputJobTests(LocalServicesWithoutWorker services)
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken ct = CancellationToken.None;

    [Fact]
    public async Task ScheduleTeam_EnqueuesTeamTaskPayloadWithoutCreatingOutputs()
    {
        var clock = new TestClock(BaseTime);
        var service = Outputs(clock);
        var team = ObjectId.GenerateNewId();
        var actor = ObjectId.GenerateNewId();

        var outputsBefore = await services.Store.Collection("output").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Empty, new CountOptions(), ct);
        var jobId = await service.ScheduleTeamAsync(team, actor, ct);

        var job = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument("_id", jobId)).FirstAsync(ct);
        Assert.Equal(OutputService.TeamJobKind, job.Text("kind"));
        Assert.Equal("pending", job.Text("state"));
        var payload = job["payload"].AsBsonDocument;
        Assert.Equal(team.ToString(), payload.Text("team_id"));
        Assert.Equal(actor.ToString(), payload.Text("current_user_id"));
        Assert.Equal(outputsBefore, await services.Store.Collection("output")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty, new CountOptions(), ct));
    }

    [Fact]
    public async Task RunTeamExport_HandlerBodyCreatesWorkingProjectTargetsAndQueuesProjectJobs()
    {
        var clock = new TestClock(BaseTime);
        var service = Outputs(clock);
        var team = ObjectId.GenerateNewId();
        var actor = await UserAsync();
        await services.Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = team,
            ["n"] = "team-output-" + team
        }, cancellationToken: ct);
        var working = await ProjectAsync(team, status: 0);
        var completed = await ProjectAsync(team, status: 1);
        var workingTarget = await TargetAsync(working);
        await TargetAsync(completed);

        var jobId = await service.ScheduleTeamAsync(team, actor, ct);
        var payload = (await services.Store.Collection("csharp_job")
            .Find(new BsonDocument("_id", jobId)).FirstAsync(ct))["payload"].AsBsonDocument;
        await service.RunTeamExportAsync(payload, ct);

        Assert.Equal(1, await services.Store.Collection("output")
            .CountDocumentsAsync(new BsonDocument("t", workingTarget)));
        Assert.Equal(0, await services.Store.Collection("output")
            .CountDocumentsAsync(new BsonDocument("p", completed)));
        Assert.Equal(1, await services.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument
            {
                ["kind"] = OutputService.JobKind,
                ["payload.project_id"] = working
            }));
    }

    [Fact]
    public async Task RunTeamExport_MissingTeamOrActorFailsWithoutPartialOutputs()
    {
        var service = Outputs(new TestClock(BaseTime));

        var outputsBefore = await services.Store.Collection("output").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Empty, new CountOptions(), ct);
        var missingTeam = await Assert.ThrowsAsync<ApiError>(() =>
            service.RunTeamExportAsync(new BsonDocument
            {
                ["team_id"] = ObjectId.GenerateNewId().ToString(),
                ["current_user_id"] = ObjectId.GenerateNewId().ToString()
            }, ct));
        Assert.Equal(3001, missingTeam.Code);

        var team = ObjectId.GenerateNewId();
        await services.Store.Collection("team").InsertOneAsync(new BsonDocument("_id", team),
            cancellationToken: ct);
        var missingActor = await Assert.ThrowsAsync<ApiError>(() =>
            service.RunTeamExportAsync(new BsonDocument
            {
                ["team_id"] = team.ToString(),
                ["current_user_id"] = ObjectId.GenerateNewId().ToString()
            }, ct));
        Assert.Equal(5105, missingActor.Code);
        Assert.Equal(outputsBefore, await services.Store.Collection("output")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty, new CountOptions(), ct));
    }

    private OutputService Outputs(TestClock clock) => new(
        services.Store,
        services.App.Services.GetRequiredService<ProjectService>(),
        new ProjectLabelLock(services.Store, clock),
        new LabelRequestContext(),
        services.App.Services.GetRequiredService<ObjectStorage>(),
        new UserView(services.Configuration, services.App.Services),
        new JobQueue(services.Store, clock),
        services.Configuration,
        clock,
        NullLogger<OutputService>.Instance,
        services.App.Services.GetRequiredService<ProjectStorageRouter>());

    private async Task<ObjectId> UserAsync()
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("user").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "team-output-user-" + id
        }, cancellationToken: ct);
        return id;
    }

    private async Task<ObjectId> ProjectAsync(ObjectId team, int status)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "team-output-project-" + id,
            ["t"] = team,
            ["st"] = status,
            ["label_revision"] = 0L,
            ["m_e"] = BaseTime.UtcDateTime,
            ["ob"] = new BsonInt64(0),
            ["fs"] = new BsonInt64(0)
        }, cancellationToken: ct);
        return id;
    }

    private async Task<ObjectId> TargetAsync(ObjectId project)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("target").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["t"] = project,
            ["l"] = ObjectId.GenerateNewId(),
            ["tsc"] = 0,
            ["csc"] = 0,
            ["label_epoch"] = 0L
        }, cancellationToken: ct);
        return id;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
