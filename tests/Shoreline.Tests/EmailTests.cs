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
        using var mailpit = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + TestEnvironment.MailpitPort), Timeout = TimeSpan.FromSeconds(5) };
        JsonElement messages = default;
        for (var attempt = 0; attempt < 50; attempt++)
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
}
