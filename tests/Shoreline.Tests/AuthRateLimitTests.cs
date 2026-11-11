using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

/// <summary>
/// End-to-end verification of the <c>auth</c> rate limiting policy. The policy is
/// attached with <c>RequireRateLimiting("auth")</c> on the login and email-code
/// endpoints, so it only takes effect when endpoint metadata is visible to the
/// rate limiting middleware (UseRouting must run before UseRateLimiter).
/// </summary>
public sealed class AuthRateLimitTests : IAsyncLifetime
{
    private readonly string database = "shoreline_csharp_ratelimit_" + Guid.NewGuid().ToString("N");
    private readonly string storageRoot = Path.Combine(Path.GetTempPath(), "shoreline-ratelimit-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? app;
    private HttpClient? http;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(storageRoot);
        await new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}").DropDatabaseAsync(database);
        var settings = new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = $"mongodb://127.0.0.1:{TestEnvironment.MongoPort}/{database}",
            ["Auth:Secret"] = SecurityTests.Secret,
            ["Auth:AdminEmail"] = "owner@example.test",
            ["Bootstrap:Enabled"] = "false",
            ["Storage:Type"] = "Local",
            ["Storage:Root"] = storageRoot,
            ["Storage:Domain"] = "http://files.example.test",
            ["Mail:Enabled"] = "false",
            ["Archive:AllowedHosts:0"] = "api.example.test"
        };
        app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
        http = app.CreateClient(); // Force startup before the first counted request.
    }

    public async Task DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) await app.DisposeAsync();
        try { await new MongoClient($"mongodb://127.0.0.1:{TestEnvironment.MongoPort}").DropDatabaseAsync(database); }
        catch { /* best effort */ }
        try { Directory.Delete(storageRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task LoginAttempts_AreLimitedAfterTwentyRequestsPerMinute()
    {
        // The body is intentionally invalid: the limiter must reject the request
        // before endpoint validation, so a brute-force attempt cannot avoid the
        // counter by sending junk.
        for (var i = 0; i < 20; i++)
        {
            using var allowed = await PostLogin("203.0.113.7", i);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var rejected = await PostLogin("203.0.113.7", 20);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task SpoofedForwardedForHeader_DoesNotResetTheLimit()
    {
        // The host has no ForwardedHeaders middleware, so X-Forwarded-For is
        // untrusted input and must never become part of the rate limit partition.
        for (var i = 0; i < 20; i++)
        {
            using var allowed = await PostLogin("198.51.100." + i, i);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var rejected = await PostLogin("192.0.2.99", 20);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    private Task<HttpResponseMessage> PostLogin(string forwardedFor, int attempt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/user/token")
        {
            Content = JsonContent.Create(new { email = $"user{attempt}@example.test", password = "wrong-password" })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return http!.SendAsync(request);
    }
}
