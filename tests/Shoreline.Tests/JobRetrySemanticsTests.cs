using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// Proves the queue-level retry contract that JobQueueTests (queue-state only)
// does not cover: bounded attempts, backoff schedule, worker kill-switch, and
// the security-email exactly-once send fence.
[Collection("local-services")]
public sealed class JobRetrySemanticsTests(LocalServices services)
{
    [Fact]
    public void BackoffSchedule_IsBoundedExponential()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), JobQueue.RetryDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(10), JobQueue.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(20), JobQueue.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(160), JobQueue.RetryDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(300), JobQueue.RetryDelay(6));
        Assert.Equal(TimeSpan.FromSeconds(300), JobQueue.RetryDelay(7));
        Assert.Equal(TimeSpan.FromSeconds(300), JobQueue.RetryDelay(100));
        Assert.Equal(JobQueue.DefaultMaxAttempts, 5);
    }

    [Fact]
    public async Task MaxAttempts_CeilingMovesJobToFailed()
    {
        var clock = TimeProvider.System;
        var queue = new JobQueue(services.Store, clock, maxAttempts: 2);
        var id = ObjectId.GenerateNewId();
        await queue.EnqueueOnceAsync(id, "retry-sem", new BsonDocument(), default);

        var first = await queue.ClaimAsync("owner-a", ["retry-sem"], default);
        Assert.NotNull(first);
        Assert.Equal(1, first["attempts"].AsInt32);
        await queue.FailAsync(id, "owner-a", first["attempts"].AsInt32, default);
        Assert.Equal("pending", await StateAsync(id));

        // Backoff has not elapsed yet: nothing eligible.
        Assert.Null(await queue.ClaimAsync("owner-b", ["retry-sem"], default));

        // Force the backoff window to elapse and reclaim the last attempt.
        await services.Store.Collection("csharp_job").UpdateOneAsync(
            new BsonDocument("_id", id),
            Builders<BsonDocument>.Update.Set("available_at", DateTime.UtcNow.AddSeconds(-1)));
        var second = await queue.ClaimAsync("owner-b", ["retry-sem"], default);
        Assert.NotNull(second);
        Assert.Equal(2, second["attempts"].AsInt32);
        await queue.FailAsync(id, "owner-b", second["attempts"].AsInt32, default);

        Assert.Equal("failed", await StateAsync(id));
        Assert.Null(await queue.ClaimAsync("owner-c", ["retry-sem"], default));
    }

    [Fact]
    public async Task FailAsync_SchedulesBoundedBackoff()
    {
        var before = DateTime.UtcNow;
        var queue = new JobQueue(services.Store, TimeProvider.System);
        var id = ObjectId.GenerateNewId();
        await queue.EnqueueOnceAsync(id, "retry-sem-backoff", new BsonDocument(), default);
        var claimed = await queue.ClaimAsync("owner-a", ["retry-sem-backoff"], default);
        Assert.NotNull(claimed);
        await queue.FailAsync(id, "owner-a", claimed["attempts"].AsInt32, default);
        var row = await services.Store.Collection("csharp_job")
            .Find(new BsonDocument("_id", id)).SingleAsync();
        var availableAt = row["available_at"].ToUniversalTime();
        var delay = availableAt - before;
        Assert.True(delay >= TimeSpan.FromSeconds(9), $"backoff too short: {delay}");
        Assert.True(delay <= TimeSpan.FromSeconds(60), $"backoff too long: {delay}");
    }

    [Fact]
    public async Task DisabledWorker_ConsumesNothing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:WorkerEnabled"] = "false"
        }).Build();
        var stub = new StubHandler("stub-disabled");
        var worker = new JobWorker(
            new JobQueue(services.Store, TimeProvider.System),
            [stub], config, NullLogger<JobWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var id = ObjectId.GenerateNewId();
            await new JobQueue(services.Store, TimeProvider.System)
                .EnqueueOnceAsync(id, "stub-disabled", new BsonDocument(), default);
            await Task.Delay(1500);
            Assert.Equal(0, stub.Calls);
            Assert.Equal("pending", await StateAsync(id));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EnabledWorker_CompletesClaimedJob()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var stub = new StubHandler("stub-enabled");
        var queue = new JobQueue(services.Store, TimeProvider.System);
        var worker = new JobWorker(queue, [stub], config, NullLogger<JobWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var id = ObjectId.GenerateNewId();
            await queue.EnqueueOnceAsync(id, "stub-enabled", new BsonDocument(), default);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await StateAsync(id) != "done" && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Assert.Equal("done", await StateAsync(id));
            Assert.Equal(1, stub.Calls);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SecurityEmail_ReplayAfterSend_DoesNotResend()
    {
        var codes = services.App.Services.GetRequiredService<EmailCodeService>();
        var config = services.App.Services.GetRequiredService<IConfiguration>();
        var email = "replay-" + Guid.NewGuid().ToString("N") + "@example.test";
        await codes.IssueAsync(2, email, default);
        var jobs = services.Store.Collection("csharp_job");
        var row = await jobs.Find(new BsonDocument("code_key", "2:" + email)).SingleAsync();
        var payload = new BsonDocument { ["code_job_id"] = row["_id"] };
        var handler = new SecurityEmailHandler(services.Store, config, TimeProvider.System);

        // Whoever sends first (background worker or direct invoke), exactly one
        // mail must exist before replaying.
        await handler.ExecuteAsync(payload, default);
        await WaitForMailCountAsync(email, 1, TimeSpan.FromSeconds(15));

        // Simulate redelivery: lease-expiry replay AND crash-between-send-and-
        // Complete. Neither may produce a second mail.
        await handler.ExecuteAsync(payload, default);
        await handler.ExecuteAsync(payload, default);
        await Task.Delay(1000);
        Assert.Equal(1, await MailCountAsync(email));

        var updated = await jobs.Find(new BsonDocument("code_key", "2:" + email)).SingleAsync();
        Assert.True(updated["verification"].AsBsonDocument["sent"].AsBoolean);
    }

    private async Task<string> StateAsync(ObjectId id) =>
        (await services.Store.Collection("csharp_job")
            .Find(new BsonDocument("_id", id)).SingleAsync())["state"].AsString;

    private static async Task WaitForMailCountAsync(string email, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await MailCountAsync(email) >= expected) return;
            await Task.Delay(200);
        }
        Assert.Fail($"Timed out waiting for {expected} mail(s) to {email}");
    }

    private static async Task<int> MailCountAsync(string email)
    {
        using var mailpit = new HttpClient { BaseAddress = new Uri(TestEnvironment.MailpitBaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        var messages = await mailpit.GetFromJsonAsync<JsonElement>(
            "/api/v1/search?query=" + Uri.EscapeDataString("to:" + email));
        return messages.GetProperty("messages").GetArrayLength();
    }

    private sealed class StubHandler(string kind) : IJobHandler
    {
        public int Calls;
        public string Kind => kind;
        public Task ExecuteAsync(BsonDocument payload, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }
}
