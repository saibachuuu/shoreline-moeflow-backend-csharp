using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Modules.PartnerSearch;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

/// <summary>
/// HTTP-level coverage. When the host Program.cs maps the module (the private
/// integration snapshot used for the compatibility audit) these tests exercise
/// the real Program pipeline. In the shared tree, where this module is not wired
/// yet, they fall back to a minimal host built from the same services and the
/// same ApiError/CORS pipeline, so the endpoints are still tested over HTTP.
/// </summary>
[Collection("local-services")]
public sealed class PartnerSearchHttpTests(LocalServices services)
{
    [Fact]
    public async Task Options_works_without_origin_and_keeps_global_cors()
    {
        await using var runtime = await PartnerSearchHttpRuntime.CreateAsync(services);

        using var plain = await runtime.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/v1/partner-search-query-entry"));
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        using var settings = await runtime.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/v1/partner-search-query-entry/settings"));
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/partner-search-query-entry");
        preflight.Headers.Add("Origin", "http://localhost");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var response = await runtime.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Settings_requires_an_admin_token()
    {
        await using var runtime = await PartnerSearchHttpRuntime.CreateAsync(services);
        var (admin, member) = await runtime.SeedUsersAsync();

        using var missing = await runtime.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/v1/partner-search-query-entry/settings"));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        var missingBody = await ReadJsonAsync(missing);
        Assert.Equal("NeedTokenError", missingBody.GetProperty("error").GetString());
        Assert.Equal(1003, missingBody.GetProperty("code").GetInt32());

        using var denied = await runtime.Client.SendAsync(Request(HttpMethod.Get,
            "/v1/partner-search-query-entry/settings", member));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedBody = await ReadJsonAsync(denied);
        Assert.Equal("NoPermissionError", deniedBody.GetProperty("error").GetString());
        Assert.Equal(103, deniedBody.GetProperty("code").GetInt32());

        using var allowed = await runtime.Client.SendAsync(Request(HttpMethod.Get,
            "/v1/partner-search-query-entry/settings", admin));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var body = await ReadJsonAsync(allowed);
        Assert.False(body.GetProperty("partner_search_enabled").GetBoolean());
        Assert.Equal(0, body.GetProperty("partner_search_team_ids").GetArrayLength());
        Assert.Equal(10, body.GetProperty("partner_search_rate_limit_seconds").GetInt32());
        Assert.Equal(20, body.GetProperty("partner_search_max_limit").GetInt32());
    }

    [Fact]
    public async Task Settings_put_is_partial_and_preserves_the_shared_site_document()
    {
        await using var runtime = await PartnerSearchHttpRuntime.CreateAsync(services);
        var (admin, _) = await runtime.SeedUsersAsync();
        var team = await runtime.InsertTeamAsync("http team");
        await runtime.Store.Collection("site_setting").InsertOneAsync(new BsonDocument
        {
            ["n"] = "site",
            ["title"] = "Core title",
            ["notification"] = new BsonDocument("enabled", true),
        });

        using var update = await runtime.Client.SendAsync(Request(HttpMethod.Put,
            "/v1/partner-search-query-entry/settings", admin,
            $$"""{"partner_search_enabled":true,"partner_search_team_ids":["{{team}}"],"partner_search_rate_limit_seconds":7}"""));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var body = await ReadJsonAsync(update);
        Assert.True(body.GetProperty("partner_search_enabled").GetBoolean());
        Assert.Equal(team.ToString(), body.GetProperty("partner_search_team_ids")[0].GetString());
        Assert.Equal(7, body.GetProperty("partner_search_rate_limit_seconds").GetInt32());

        var document = await runtime.Store.Collection("site_setting")
            .Find(new BsonDocument("n", "site")).FirstAsync();
        Assert.Equal("Core title", document["title"].AsString);
        Assert.True(document["notification"].AsBsonDocument["enabled"].AsBoolean);
        Assert.True(document["pse"].AsBoolean);

        using var invalid = await runtime.Client.SendAsync(Request(HttpMethod.Put,
            "/v1/partner-search-query-entry/settings", admin,
            """{"partner_search_max_limit":0}"""));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await ReadJsonAsync(invalid);
        Assert.Equal("ValidateError", invalidBody.GetProperty("error").GetString());
        Assert.Equal(2, invalidBody.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Disabled_query_returns_403_code_1_even_for_malformed_json()
    {
        await using var runtime = await PartnerSearchHttpRuntime.CreateAsync(services);
        await runtime.SetSettingsAsync(enabled: false);

        using var response = await runtime.Client.SendAsync(Request(HttpMethod.Post,
            "/v1/partner-search-query-entry", json: "{}"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("PartnerSearchDisabledError", body.GetProperty("error").GetString());
        Assert.Equal(1, body.GetProperty("code").GetInt32());
        Assert.Equal("partner search is disabled", body.GetProperty("message").GetString());

        using var malformed = await runtime.Client.SendAsync(Request(HttpMethod.Post,
            "/v1/partner-search-query-entry", json: "{"));
        Assert.Equal(HttpStatusCode.Forbidden, malformed.StatusCode);
    }

    [Fact]
    public async Task Enabled_query_returns_projects_and_echoes_limit_and_page()
    {
        await using var runtime = await PartnerSearchHttpRuntime.CreateAsync(services);
        var team = await runtime.InsertTeamAsync("roundtrip team");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);
        var project = await runtime.InsertProjectAsync(team, "Straße");

        using var found = await runtime.Client.SendAsync(Request(HttpMethod.Post,
            "/v1/partner-search-query-entry", json: """{"keyword":"STRASSE"}"""));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        var body = await ReadJsonAsync(found);
        Assert.Equal(1, body.GetProperty("total").GetInt64());
        var row = body.GetProperty("projects")[0];
        Assert.Equal(project.ToString(), row.GetProperty("id").GetString());
        Assert.Equal("Straße", row.GetProperty("name").GetString());
        Assert.Equal("source", row.GetProperty("source_name").GetString());
        Assert.Equal("target", row.GetProperty("target_name").GetString());
        Assert.Equal(team.ToString(), row.GetProperty("team_id").GetString());
        Assert.Equal("roundtrip team", row.GetProperty("team_name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("thumbnail_url").ValueKind);

        using var empty = await runtime.Client.SendAsync(Request(HttpMethod.Post,
            "/v1/partner-search-query-entry", json: """{"keyword":"","limit":5,"page":2}"""));
        var emptyBody = await ReadJsonAsync(empty);
        Assert.Equal(0, emptyBody.GetProperty("total").GetInt64());
        Assert.Equal(5, emptyBody.GetProperty("limit").GetInt32());
        Assert.Equal(2, emptyBody.GetProperty("page").GetInt64());
        Assert.Equal(0, emptyBody.GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task Throttle_uses_forwarded_ip_and_client_key_over_http()
    {
        await using var runtime = await PartnerSearchHttpRuntime.CreateAsync(services);
        await runtime.SetSettingsAsync(enabled: true, teams: [], rateLimitSeconds: 10);

        using var first = await runtime.Client.SendAsync(Query("abc", "1.2.3.4, 9.9.9.9"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var limited = await runtime.Client.SendAsync(Query("abc", "1.2.3.4"));
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        var body = await ReadJsonAsync(limited);
        Assert.Equal("PartnerSearchRateLimitedError", body.GetProperty("error").GetString());
        Assert.Equal(2, body.GetProperty("code").GetInt32());
        Assert.Equal("rate limited, please retry later", body.GetProperty("message").GetString());

        using var otherKey = await runtime.Client.SendAsync(Query("xyz", "1.2.3.4"));
        Assert.Equal(HttpStatusCode.OK, otherKey.StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token = null,
        string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage Query(string clientKey, string forwardedFor)
    {
        var request = Request(HttpMethod.Post, "/v1/partner-search-query-entry",
            json: $$"""{"client_key":"{{clientKey}}"}""");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

internal sealed class PartnerSearchHttpRuntime : IAsyncDisposable
{
    private readonly string databaseName;
    private readonly WebApplicationFactory<Program>? factory;
    private readonly WebApplication? app;

    public HttpClient Client { get; }
    public MongoStore Store { get; }
    public LegacyToken Tokens { get; }
    public bool UsesProgram { get; }

    private PartnerSearchHttpRuntime(string databaseName, HttpClient client, MongoStore store,
        LegacyToken tokens, WebApplicationFactory<Program>? factory, WebApplication? app, bool usesProgram)
    {
        this.databaseName = databaseName;
        this.factory = factory;
        this.app = app;
        Client = client;
        Store = store;
        Tokens = tokens;
        UsesProgram = usesProgram;
    }

    public static async Task<PartnerSearchHttpRuntime> CreateAsync(LocalServices services)
    {
        var databaseName = "shoreline_partner_search_http_" + Guid.NewGuid().ToString("N");
        var values = services.Configuration.AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        values["Mongo:Uri"] = "mongodb://127.0.0.1:27128/" + databaseName;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(values));
        });
        var client = factory.CreateClient();
        using (var probe = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/v1/partner-search-query-entry")))
        {
            if (probe.StatusCode != HttpStatusCode.NotFound)
            {
                return new PartnerSearchHttpRuntime(databaseName, client,
                    factory.Services.GetRequiredService<MongoStore>(),
                    factory.Services.GetRequiredService<LegacyToken>(),
                    factory, null, usesProgram: true);
            }
        }

        client.Dispose();
        await factory.DisposeAsync();
        await DropAsync(databaseName);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MongoStore>();
        builder.Services.AddSingleton<ObjectStorage>();
        builder.Services.AddSingleton<LegacyToken>();
        builder.Services.AddSingleton<CurrentUser>();
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins("http://localhost")
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "X-Label-Revision", "X-Request-ID", "Idempotency-Key")));
        builder.Services.AddPartnerSearchServices();
        var app = builder.Build();
        app.UseCors();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (ApiError error)
            {
                context.Response.StatusCode = error.Status;
                var body = new Dictionary<string, object?>
                {
                    ["error"] = error.Error,
                    ["code"] = error.Code,
                    ["message"] = error.ResponseMessage,
                };
                if (error.IdentityCode is not null) body["identity_code"] = error.IdentityCode;
                await context.Response.WriteAsJsonAsync(body);
            }
            catch (BadHttpRequestException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "ValidateError",
                    code = 2,
                    message = "Invalid request",
                });
            }
        });
        app.MapPartnerSearch();
        await app.Services.GetRequiredService<MongoStore>().InitializeAsync(default);
        await app.StartAsync();
        return new PartnerSearchHttpRuntime(databaseName, app.GetTestClient(),
            app.Services.GetRequiredService<MongoStore>(),
            app.Services.GetRequiredService<LegacyToken>(), null, app, usesProgram: false);
    }

    public async Task<(string Admin, string Member)> SeedUsersAsync()
    {
        var admin = await InsertUserAsync(admin: true);
        var member = await InsertUserAsync(admin: false);
        return (admin, member);
    }

    public Task SetSettingsAsync(bool enabled, ObjectId[]? teams = null,
        int rateLimitSeconds = 10, int maxLimit = 20) =>
        Store.Collection("site_setting").UpdateOneAsync(
            new BsonDocument("n", "site"),
            new BsonDocument("$set", new BsonDocument
            {
                ["n"] = "site",
                ["pse"] = enabled,
                ["pst"] = new BsonArray((teams ?? []).Select(id => (BsonValue)id)),
                ["psq"] = rateLimitSeconds,
                ["psm"] = maxLimit,
            }),
            new UpdateOptions { IsUpsert = true });

    public async Task<ObjectId> InsertTeamAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        await Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["i"] = "",
        });
        return id;
    }

    public async Task<ObjectId> InsertProjectAsync(ObjectId team, string name)
    {
        var id = ObjectId.GenerateNewId();
        await Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["i"] = "intro",
            ["sn"] = "source",
            ["tn"] = "target",
            ["t"] = team,
            ["st"] = 0,
            ["m_e"] = DateTime.UtcNow,
        });
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        await DropAsync(databaseName);
    }

    private async Task<string> InsertUserAsync(bool admin)
    {
        var id = ObjectId.GenerateNewId();
        var password = PasswordHash.Create("test-password");
        await Store.Collection("user").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["e"] = Guid.NewGuid().ToString("N") + "@example.test",
            ["n"] = Guid.NewGuid().ToString("N"),
            ["p"] = password,
            ["admin"] = admin,
        });
        return Tokens.Issue(id.ToString(), password);
    }

    private static Task DropAsync(string databaseName) =>
        new MongoClient("mongodb://127.0.0.1:27128").DropDatabaseAsync(databaseName);
}
