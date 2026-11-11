using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;

namespace Shoreline.Tests;

// Production deploy-shape coverage: the Jobs:WorkerEnabled split-role
// toggle, the 30s graceful-shutdown budget, the health endpoints, and the
// prod compose example. Everything here targets the disposable loopback
// stack only (TestEnvironment ports); no remote host is ever touched.
public sealed class WorkerToggleTests
{
    private sealed class ProbeHandler : IJobHandler
    {
        public string Kind => "deploy-toggle-probe";
        public Task ExecuteAsync(BsonDocument payload, CancellationToken ct) => Task.CompletedTask;
    }

    private static (JobWorker Worker, JobQueue Queue, string Database) Create(string? flag, bool withHandler)
    {
        var database = "shoreline_worker_toggle_" + Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = TestEnvironment.MongoUri(database),
        };
        if (flag is not null)
            settings["Jobs:WorkerEnabled"] = flag;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var store = new MongoStore(configuration);
        var queue = new JobQueue(store, TimeProvider.System);
        var handlers = withHandler ? new IJobHandler[] { new ProbeHandler() } : [];
        var worker = new JobWorker(queue, handlers, configuration, NullLogger<JobWorker>.Instance);
        return (worker, queue, database);
    }

    private static async Task<string> JobStateAsync(string database, ObjectId id, CancellationToken ct)
    {
        using var client = new MongoClient(TestEnvironment.MongoUri(database));
        var row = await client.GetDatabase(database).GetCollection<BsonDocument>("csharp_job")
            .Find(new BsonDocument("_id", id)).FirstAsync(ct);
        return row["state"].AsString;
    }

    private static async Task DropAsync(string database)
    {
        using var client = new MongoClient(TestEnvironment.MongoUri(database));
        await client.DropDatabaseAsync(database);
    }

    [Fact]
    public async Task DisabledWorker_LeavesEnqueuedJobPending()
    {
        var (worker, queue, database) = Create("false", withHandler: true);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            var id = await queue.EnqueueAsync("deploy-toggle-probe", new BsonDocument(), CancellationToken.None);
            // The poll interval is 500ms; a running worker would claim the job
            // almost immediately. A disabled worker never polls, so pending
            // after this wait is deterministic, not timing-sensitive.
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal("pending", await JobStateAsync(database, id, CancellationToken.None));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task DefaultWorker_DrainsEnqueuedJob()
    {
        // No Jobs:WorkerEnabled key: the historical single-process default.
        var (worker, queue, database) = Create(null, withHandler: true);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            var id = await queue.EnqueueAsync("deploy-toggle-probe", new BsonDocument(), CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            string state;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                state = await JobStateAsync(database, id, CancellationToken.None);
            } while (state != "done" && DateTime.UtcNow < deadline);
            Assert.Equal("done", state);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task UnparsableValue_KeepsWorkerEnabled()
    {
        var (worker, queue, database) = Create("not-a-bool", withHandler: true);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            var id = await queue.EnqueueAsync("deploy-toggle-probe", new BsonDocument(), CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            string state;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
                state = await JobStateAsync(database, id, CancellationToken.None);
            } while (state != "done" && DateTime.UtcNow < deadline);
            Assert.Equal("done", state);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await DropAsync(database);
        }
    }

    [Fact]
    public async Task DisabledWorker_StopsWithoutStartingHostedLifetime()
    {
        // A disabled worker returns a completed task from StartAsync, so the
        // generic host never tracks a running background operation for it.
        var (worker, _, database) = Create("false", withHandler: true);
        try
        {
            var start = worker.StartAsync(CancellationToken.None);
            Assert.True(start.IsCompletedSuccessfully);
            await start;
        }
        finally
        {
            await DropAsync(database);
        }
    }
}

[Collection("local-services")]
public sealed class ShutdownTimeoutTests(LocalServices services)
{
    [Fact]
    public void ShutdownBudget_IsThirtySeconds()
    {
        // The runbook's graceful-shutdown procedure (SIGTERM drain) depends on
        // this budget: in-flight jobs keep their lease while the host stops.
        var options = services.App.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(30), options.ShutdownTimeout);
    }
}

[Collection("local-services")]
public sealed class DeployHealthTests(LocalServices services)
{
    [Fact]
    public async Task Ping_ReturnsPong()
    {
        Assert.Equal("pong", await services.Http.GetStringAsync("/ping", CancellationToken.None));
    }

    [Fact]
    public async Task LiveEndpoint_ReportsOkWithoutDependencies()
    {
        using var response = await services.Http.GetAsync("/health/live", CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadyEndpoint_PingsMongo()
    {
        using var response = await services.Http.GetAsync("/health/ready", CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync(CancellationToken.None));
    }
}

public sealed class DeployComposeTests
{
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "compose.prod.example.yml"));

    private static string ReadCompose()
    {
        var path = RepoRoot();
        Assert.True(File.Exists(path), "compose.prod.example.yml must exist at the repo root");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ApiRole_DisablesWorker()
    {
        var text = ReadCompose();
        Assert.Contains("Jobs__WorkerEnabled", text);
        // The api service block sets false; the worker block sets true.
        var apiIndex = text.IndexOf("api:", StringComparison.Ordinal);
        var workerIndex = text.IndexOf("worker:", StringComparison.Ordinal);
        Assert.True(apiIndex >= 0 && workerIndex > apiIndex);
        var apiBlock = text[apiIndex..workerIndex];
        Assert.Contains("Jobs__WorkerEnabled: \"false\"", apiBlock);
        Assert.Contains("Jobs__WorkerEnabled: \"true\"", text[workerIndex..]);
    }

    [Fact]
    public void Compose_UsesExternalPlaceholdersNotLocalServices()
    {
        var text = ReadCompose();
        // Production MongoDB/RabbitMQ are external: no local services, only
        // unset-variable guards that fail `compose config` with a message.
        Assert.DoesNotContain("image: mongo", text);
        Assert.DoesNotContain("image: rabbitmq", text);
        Assert.Contains("MONGO_URI", text);
        Assert.Contains("RABBITMQ", text);
        Assert.Contains("SHORELINE_IMAGE", text);
    }

    [Fact]
    public void Compose_StopsGracefullyBeyondShutdownBudget()
    {
        // HostOptions.ShutdownTimeout is 30s; both roles allow extra time so
        // SIGTERM always finishes inside the budget with leases intact.
        var text = ReadCompose();
        Assert.Contains("stop_grace_period: 40s", text);
    }
}
