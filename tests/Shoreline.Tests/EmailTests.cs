using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class EmailTests(LocalServices services)
{
    [Fact]
    public async Task ConcurrentIssueCreatesOneDurableCodeAndWorkerDeliversToMailpit()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var codes = services.App.Services.GetRequiredService<EmailCodeService>();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await codes.IssueAsync(2, email, default); return true; }
            catch (ApiError e) when (e.Code == 2004) { return false; }
        }));
        Assert.Single(results, r => r);
        var row = await services.Store.Collection("csharp_job").Find(new BsonDocument("code_key", "2:" + email)).SingleAsync();
        Assert.Equal(row["_id"], row["payload"]["code_job_id"]);
        // Rollback compatibility: the same code must be visible to the Python backend.
        var legacy = await services.Store.Collection("v_code")
            .Find(new BsonDocument { ["t"] = 2, ["i"] = email }).SingleAsync();
        Assert.Equal(row["verification"]["c"].AsString, legacy["c"].AsString);
        Assert.Equal(row["verification"]["e"].ToUniversalTime(), legacy["e"].ToUniversalTime());
        Assert.Equal(0, legacy["w"].AsInt32);
        using var mailpit = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + TestEnvironment.MailpitPort), Timeout = TimeSpan.FromSeconds(5) };
        JsonElement messages = default;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            messages = await mailpit.GetFromJsonAsync<JsonElement>("/api/v1/search?query=" + Uri.EscapeDataString("to:" + email));
            if (messages.GetProperty("messages").GetArrayLength() > 0) break;
            await Task.Delay(100);
        }
        var messageId = Assert.Single(messages.GetProperty("messages").EnumerateArray()).GetProperty("ID").GetString();
        var message = await mailpit.GetFromJsonAsync<JsonElement>("/api/v1/message/" + messageId);
        Assert.Contains(row["verification"]["c"].AsString, message.GetProperty("Text").GetString());
    }

    [Fact]
    public async Task CodeIsSingleUseAndFiveConcurrentWrongAttemptsExhaustIt()
    {
        var codes = services.App.Services.GetRequiredService<EmailCodeService>();
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        await codes.IssueAsync(4, email, default);
        var row = await services.Store.Collection("csharp_job").Find(new BsonDocument("code_key", "4:" + email)).SingleAsync();
        var content = row["verification"]["c"].AsString;
        var successes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await codes.VerifyAsync(4, email, content, "v_code", true, default); return true; }
            catch (ApiError) { return false; }
        }));
        Assert.Single(successes, r => r);
        Assert.Equal(0, await services.Store.Collection("v_code")
            .CountDocumentsAsync(new BsonDocument { ["t"] = 4, ["i"] = email }));
        var otherEmail = Guid.NewGuid().ToString("N") + "@example.test";
        await codes.IssueAsync(4, otherEmail, default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Assert.ThrowsAsync<ApiError>(
            () => codes.VerifyAsync(4, otherEmail, "invalid", "v_code", true, default))));
        var exhausted = await services.Store.Collection("csharp_job").Find(new BsonDocument("code_key", "4:" + otherEmail)).SingleAsync();
        Assert.Equal(5, exhausted["verification"]["w"].AsInt32);
        await Assert.ThrowsAsync<ApiError>(() => codes.VerifyAsync(4, otherEmail, exhausted["verification"]["c"].AsString, "v_code", true, default));
    }

    [Fact]
    public async Task ResetPasswordAcceptsLegacyCodeInvalidatesOldTokenAndCannotReplay()
    {
        var id = ObjectId.GenerateNewId();
        var email = id + "@example.test";
        var hash = PasswordHash.Create("old-password");
        await services.Store.Collection("user").InsertOneAsync(new BsonDocument
        { ["_id"] = id, ["e"] = email, ["n"] = "u" + id, ["p"] = hash });
        await services.Store.Collection("v_code").InsertOneAsync(new BsonDocument
        { ["_cls"] = "VCode", ["t"] = 4, ["i"] = email, ["c"] = "123456", ["w"] = 0, ["e"] = DateTime.UtcNow.AddMinutes(10) });
        using var oldClient = services.App.CreateClient();
        oldClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(id.ToString(), hash));
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/user/password")
        { Content = JsonContent.Create(new { email, password = "new-password", v_code = "123456" }) };
        using var response = await services.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var invalidated = await oldClient.GetAsync("/v1/user/info");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidated.StatusCode);
        using var newClient = services.App.CreateClient();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        newClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString());
        using var valid = await newClient.GetAsync("/v1/user/info");
        valid.EnsureSuccessStatusCode();
        using var replay = new HttpRequestMessage(HttpMethod.Delete, "/v1/user/password")
        { Content = JsonContent.Create(new { email, password = "hijacked-password", v_code = "123456" }) };
        using var rejected = await services.Http.SendAsync(replay);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task ExpiredAndSupersededCodesCannotBeUsed()
    {
        var codes = services.App.Services.GetRequiredService<EmailCodeService>();
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        await codes.IssueAsync(2, email, default);
        var jobs = services.Store.Collection("csharp_job");
        var filter = new BsonDocument("code_key", "2:" + email);
        var row = await jobs.Find(filter).SingleAsync();
        await jobs.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("verification.e", DateTime.UtcNow.AddMinutes(-1)));
        await Assert.ThrowsAsync<ApiError>(() => codes.VerifyAsync(2, email, row["verification"]["c"].AsString, "v_code", true, default));
        // A new code remains authoritative even if an older Python code with the same identity still exists.
        await services.Store.Collection("v_code").InsertOneAsync(new BsonDocument
        { ["t"] = 2, ["i"] = email, ["c"] = "legacy", ["w"] = 0, ["e"] = DateTime.UtcNow.AddHours(1) });
        await Assert.ThrowsAsync<ApiError>(() => codes.VerifyAsync(2, email, "legacy", "v_code", true, default));
    }

    [Fact]
    public async Task SecurityEmailHandler_Validates_Preconditions()
    {
        var clock = TimeProvider.System;
        var disabledConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mail:Enabled"] = "false"
        }).Build();

        var handlerWithDisabledMail = new SecurityEmailHandler(services.Store, disabledConfig, clock);
        var jobId = ObjectId.GenerateNewId();
        await services.Store.Collection("csharp_job").InsertOneAsync(new BsonDocument
        {
            ["_id"] = jobId,
            ["verification"] = new BsonDocument
            {
                ["consumed"] = false,
                ["w"] = 0,
                ["e"] = DateTime.UtcNow.AddHours(1),
                ["t"] = 2,
                ["i"] = "test@example.test",
                ["c"] = "123456"
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handlerWithDisabledMail.ExecuteAsync(new BsonDocument("code_job_id", jobId), default));

        // When consumed is true, it should return silently
        await services.Store.Collection("csharp_job").UpdateOneAsync(
            new BsonDocument("_id", jobId),
            Builders<BsonDocument>.Update.Set("verification.consumed", true));
        await handlerWithDisabledMail.ExecuteAsync(new BsonDocument("code_job_id", jobId), default);
    }

    // IssueAsync writes the durable job first and mirrors `v_code` afterwards. A
    // process death between the two writes leaves a claimable job whose code has
    // no legacy mirror. These fixtures reconstruct exactly that state; they never
    // rely on a real two-document transaction, which standalone Mongo cannot give.
    private static BsonDocument PersistedSecurityJob(ObjectId jobId, int type, string email, string code, DateTime issued)
        => new()
        {
            ["_id"] = jobId,
            ["kind"] = "security-email",
            ["payload"] = new BsonDocument("code_job_id", jobId),
            ["code_key"] = type + ":" + email,
            // A worker owns the row during execution, so the shared background
            // worker cannot claim the same fixture and make delivery counts racy.
            ["state"] = "running",
            ["owner"] = "fixture-owner",
            ["lease_until"] = DateTime.UtcNow.AddMinutes(30),
            ["attempts"] = 1,
            ["available_at"] = issued,
            ["created_at"] = issued,
            ["verification"] = new BsonDocument
            {
                ["t"] = type, ["i"] = email, ["c"] = code, ["e"] = issued.AddHours(1),
                ["s"] = issued, ["w"] = 0, ["consumed"] = false
            }
        };

    private SecurityEmailHandler Handler() =>
        new(services.Store, services.Configuration, TimeProvider.System);

    // BSON stores dates with millisecond precision.
    private static DateTime BsonDate(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static async Task<JsonElement> SearchMailpitAsync(string email)
    {
        using var mailpit = new HttpClient
        {
            BaseAddress = new Uri(TestEnvironment.MailpitBaseUrl),
            Timeout = TimeSpan.FromSeconds(5)
        };
        return await mailpit.GetFromJsonAsync<JsonElement>(
            "/api/v1/search?query=" + Uri.EscapeDataString("to:" + email));
    }

    // Deterministic stand-in for a Mongo write failpoint: only this fixture's
    // legacy mirror document is rejected, so the v_code upsert fails server-side.
    private async Task SetMirrorWriteFailpointAsync(string email)
    {
        try { await services.Store.Database.CreateCollectionAsync("v_code"); }
        catch (MongoCommandException e) when (e.Code == 48) { /* NamespaceExists */ }
        await services.Store.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["collMod"] = "v_code",
            ["validator"] = new BsonDocument { ["i"] = new BsonDocument("$ne", email) },
            ["validationLevel"] = "strict",
            ["validationAction"] = "error"
        });
    }

    private Task ClearMirrorWriteFailpointAsync() =>
        services.Store.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["collMod"] = "v_code",
            ["validator"] = new BsonDocument()
        });

    [Fact]
    public async Task CrashWindowJobWithoutLegacyMirrorNeverSendsUntilMirrorIsRecovered()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var issued = DateTime.UtcNow;
        var jobId = ObjectId.GenerateNewId();
        var jobs = services.Store.Collection("csharp_job");
        await jobs.InsertOneAsync(PersistedSecurityJob(jobId, 2, email, "654321", issued));
        Assert.Equal(0, await services.Store.Collection("v_code")
            .CountDocumentsAsync(new BsonDocument { ["t"] = 2, ["i"] = email }));

        await SetMirrorWriteFailpointAsync(email);
        try
        {
            // The mirror cannot be made durable, so no delivery may happen; the
            // failure must surface to the worker (which retries the same job id).
            Exception? failure = null;
            try { await Handler().ExecuteAsync(new BsonDocument("code_job_id", jobId), default); }
            catch (Exception ex) { failure = ex; }

            Assert.Equal(0, await services.Store.Collection("v_code")
                .CountDocumentsAsync(new BsonDocument { ["t"] = 2, ["i"] = email }));
            var blocked = await SearchMailpitAsync(email);
            Assert.Equal(0, blocked.GetProperty("messages").GetArrayLength());
            Assert.IsType<MongoWriteException>(failure);
        }
        finally
        {
            await ClearMirrorWriteFailpointAsync();
        }

        // Replay after the fault clears: the handler recreates the exact mirror
        // from the durable job (same code, no rotation), then delivers.
        await Handler().ExecuteAsync(new BsonDocument("code_job_id", jobId), default);
        var mirror = await services.Store.Collection("v_code")
            .Find(new BsonDocument { ["t"] = 2, ["i"] = email }).SingleAsync();
        Assert.Equal("654321", mirror["c"].AsString);
        Assert.Equal(BsonDate(issued.AddHours(1)), mirror["e"].ToUniversalTime());
        Assert.Equal(0, mirror["w"].AsInt32);
        var row = await jobs.Find(new BsonDocument("_id", jobId)).SingleAsync();
        Assert.Equal("654321", row["verification"]["c"].AsString);

        JsonElement messages = default;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            messages = await SearchMailpitAsync(email);
            if (messages.GetProperty("messages").GetArrayLength() > 0) break;
            await Task.Delay(100);
        }
        var messageId = Assert.Single(messages.GetProperty("messages").EnumerateArray())
            .GetProperty("ID").GetString();
        using var mailpit = new HttpClient
        {
            BaseAddress = new Uri(TestEnvironment.MailpitBaseUrl), Timeout = TimeSpan.FromSeconds(5)
        };
        var message = await mailpit.GetFromJsonAsync<JsonElement>("/api/v1/message/" + messageId);
        Assert.Contains("654321", message.GetProperty("Text").GetString());

        // The recovered mirror must be usable by the rollback path as-is.
        var codes = services.App.Services.GetRequiredService<EmailCodeService>();
        await codes.VerifyAsync(2, email, "654321", "v_code", true, default);
        Assert.Equal(0, await services.Store.Collection("v_code")
            .CountDocumentsAsync(new BsonDocument { ["t"] = 2, ["i"] = email }));
    }

    [Fact]
    public async Task ReplayedJobRecoversOneMirrorWithoutResending()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var issued = DateTime.UtcNow;
        var jobId = ObjectId.GenerateNewId();
        var jobs = services.Store.Collection("csharp_job");
        await jobs.InsertOneAsync(PersistedSecurityJob(jobId, 3, email, "112233", issued));

        // Two executions model a retry after an interruption (send happened but the
        // completion write did not, so the lease expired and the job was reclaimed).
        // The verification.sent claim makes the second execution a no-op.
        await Handler().ExecuteAsync(new BsonDocument("code_job_id", jobId), default);
        await Handler().ExecuteAsync(new BsonDocument("code_job_id", jobId), default);

        var mirrors = await services.Store.Collection("v_code")
            .Find(new BsonDocument { ["t"] = 3, ["i"] = email }).ToListAsync();
        var only = Assert.Single(mirrors);
        Assert.Equal("112233", only["c"].AsString);
        Assert.Equal(BsonDate(issued.AddHours(1)), only["e"].ToUniversalTime());
        var row = await jobs.Find(new BsonDocument("_id", jobId)).SingleAsync();
        Assert.Equal("112233", row["verification"]["c"].AsString);

        JsonElement messages = default;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            messages = await SearchMailpitAsync(email);
            if (messages.GetProperty("messages").GetArrayLength() >= 1) break;
            await Task.Delay(100);
        }
        await Task.Delay(300);
        messages = await SearchMailpitAsync(email);
        // Exactly-once delivery: the replay must not resend, and the code
        // must never rotate.
        var messageId = Assert.Single(messages.GetProperty("messages").EnumerateArray())
            .GetProperty("ID").GetString();
        using var mailpit = new HttpClient
        {
            BaseAddress = new Uri(TestEnvironment.MailpitBaseUrl), Timeout = TimeSpan.FromSeconds(5)
        };
        var message = await mailpit.GetFromJsonAsync<JsonElement>("/api/v1/message/" + messageId);
        Assert.Contains("112233", message.GetProperty("Text").GetString());
    }

    [Fact]
    public async Task ReplayedJobForConsumedCodeDoesNotResurrectLegacyMirror()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var issued = DateTime.UtcNow;
        var jobId = ObjectId.GenerateNewId();
        var job = PersistedSecurityJob(jobId, 4, email, "778899", issued);
        job["verification"]["consumed"] = true;
        await services.Store.Collection("csharp_job").InsertOneAsync(job);
        // Consuming a code deletes its legacy mirror; a replayed job must not
        // recreate a usable code for a request that already completed.
        Assert.Equal(0, await services.Store.Collection("v_code")
            .CountDocumentsAsync(new BsonDocument { ["t"] = 4, ["i"] = email }));

        await Handler().ExecuteAsync(new BsonDocument("code_job_id", jobId), default);
        Assert.Equal(0, await services.Store.Collection("v_code")
            .CountDocumentsAsync(new BsonDocument { ["t"] = 4, ["i"] = email }));
        var messages = await SearchMailpitAsync(email);
        Assert.Equal(0, messages.GetProperty("messages").GetArrayLength());
    }
}
