using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

/// <summary>
/// Python's OCR implementation is explicitly disabled
/// (app/tasks/ocr.py:421 `raise NotImplementedError("OCR under reconstruction")`).
/// The C# routes must fail closed instead of queueing or returning success.
/// </summary>
[Collection("local-services")]
public sealed class OcrHttpTests(LocalServices services)
{
    [Fact]
    public async Task OcrRoutes_ReturnExplicit501AndDoNotMutateState()
    {
        var userId = ObjectId.GenerateNewId();
        var projectId = ObjectId.GenerateNewId();
        var fileId = ObjectId.GenerateNewId();
        var password = PasswordHash.Create("test-password");
        await services.Store.Collection("user").InsertOneAsync(new BsonDocument
        {
            ["_id"] = userId,
            ["e"] = "ocr-" + userId + "@example.test",
            ["n"] = "o" + userId.ToString()[12..],
            ["p"] = password,
            ["admin"] = false,
            ["b"] = false,
            ["c"] = DateTime.UtcNow
        });
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "OCR disabled project",
            ["st"] = 0,
            ["ct"] = DateTime.UtcNow
        });
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = fileId,
            ["p"] = projectId,
            ["n"] = "page.png",
            ["t"] = 2,
            ["ac"] = true,
            ["sa"] = "page.png"
        });

        using var anonymous = services.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync($"/v1/files/{fileId}/ocr", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync($"/v1/projects/{projectId}/ocr", null)).StatusCode);

        using var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(userId.ToString(), password));

        var fileResponse = await client.PostAsync($"/v1/files/{fileId}/ocr", null);
        Assert.Equal(HttpStatusCode.NotImplemented, fileResponse.StatusCode);
        var fileBody = await fileResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OcrUnavailableError", fileBody.GetProperty("error").GetString());
        Assert.Equal(5010, fileBody.GetProperty("code").GetInt32());

        var projectResponse = await client.PostAsync($"/v1/projects/{projectId}/ocr", null);
        Assert.Equal(HttpStatusCode.NotImplemented, projectResponse.StatusCode);
        var projectBody = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OcrUnavailableError", projectBody.GetProperty("error").GetString());
        Assert.Equal(5010, projectBody.GetProperty("code").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"/v1/files/{ObjectId.GenerateNewId()}/ocr", null)).StatusCode);
        var missingFile = await (await client.PostAsync($"/v1/files/{ObjectId.GenerateNewId()}/ocr", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(8001, missingFile.GetProperty("code").GetInt32());
        var missingProject = await (await client.PostAsync($"/v1/projects/{ObjectId.GenerateNewId()}/ocr", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4001, missingProject.GetProperty("code").GetInt32());

        // Fail-closed means no queue row and no parse-status transition.
        Assert.Equal(0, await services.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument("kind", new BsonDocument("$regex", "ocr"))));
        var file = await services.Store.Collection("file")
            .Find(new BsonDocument("_id", fileId)).FirstAsync();
        Assert.False(file.Contains("ps"));
        Assert.False(file.Contains("parse_status"));
        var project = await services.Store.Collection("project")
            .Find(new BsonDocument("_id", projectId)).FirstAsync();
        Assert.False(project.Contains("ocr_status"));
    }
}
