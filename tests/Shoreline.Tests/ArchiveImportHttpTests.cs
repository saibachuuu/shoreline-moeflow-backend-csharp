using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services-thumbnail")]
public sealed class ArchiveImportHttpTests(LocalServicesWithoutWorker services)
{
    private static readonly string Password = PasswordHash.Create("archive-http-test");

    [Fact]
    public async Task HttpRoutesCreateReadAndDismissAnArchiveTask()
    {
        var userId = ObjectId.GenerateNewId();
        var teamId = ObjectId.GenerateNewId();
        var projectId = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = userId,
            ["n"] = "archive-http-" + userId.ToString()[12..],
            ["e"] = userId + "@example.test",
            ["p"] = Password,
            ["admin"] = false,
            ["b"] = false,
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        await services.Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = teamId,
            ["n"] = "archive-http-team-" + teamId,
        });
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["team"] = teamId,
            ["user"] = userId,
            ["status"] = "active",
            ["base_tag"] = "creator",
            ["tags"] = new BsonArray(),
        });
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["t"] = teamId,
            ["ou"] = userId,
            ["st"] = 0,
            ["label_revision"] = 0,
        });

        var path = $"/v1/projects/{projectId}/import-task";
        using var anonymous = services.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        using var client = services.App.CreateClient();
        var tokens = services.App.Services.GetRequiredService<LegacyToken>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            tokens.Issue(userId.ToString(), Password));

        var invalid = await client.PostAsJsonAsync($"/v1/projects/{projectId}/import-from-archive",
            new { gid = "not-digits", token = "synthetic-token", gallery_url = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
            Assert.Equal("ValidateError", invalidBody.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, await services.Store.Collection("archive_import_task")
            .CountDocumentsAsync(new BsonDocument("p", projectId)));

        var create = await client.PostAsJsonAsync($"/v1/projects/{projectId}/import-from-archive",
            new { gid = "12345", token = "synthetic-token", gallery_url = "" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var task = created.RootElement.GetProperty("task");
        var taskId = ObjectId.Parse(task.GetProperty("id").GetString()!);
        Assert.Equal("12345", task.GetProperty("gid").GetString());
        Assert.Equal(0, task.GetProperty("status").GetInt32());

        var job = await services.Store.Collection("csharp_job").Find(new BsonDocument
        {
            ["kind"] = "archive_import",
            ["payload.taskid"] = taskId.ToString(),
        }).FirstOrDefaultAsync();
        Assert.NotNull(job);

        using var fetched = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        Assert.Equal(taskId.ToString(), fetched.RootElement.GetProperty("task").GetProperty("id").GetString());

        await services.Store.Collection("archive_import_task").UpdateOneAsync(
            new BsonDocument("_id", taskId),
            Builders<BsonDocument>.Update.Set("s", 5));
        var dismiss = await client.PostAsync($"/v1/projects/{projectId}/import-task/dismiss", null);
        Assert.Equal(HttpStatusCode.OK, dismiss.StatusCode);
        using var dismissed = JsonDocument.Parse(await dismiss.Content.ReadAsStringAsync());
        Assert.True(dismissed.RootElement.GetProperty("dismissed").GetBoolean());

        using var afterDismiss = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        Assert.True(afterDismiss.RootElement.GetProperty("task").GetProperty("dismissed").GetBoolean());
    }
}
