using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

/// <summary>
/// The reverse proxy support is opt-in and fail closed. These tests exercise the
/// real <c>ForwardedHeadersMiddleware</c> with our options: a listed proxy may
/// vouch for the rightmost forwarded entry only, and an unlisted peer can never
/// change the client address (which is the rate-limit partition key).
/// </summary>
public sealed class TrustedProxyTests
{
    [Fact]
    public void NoConfiguration_MeansForwardedHeadersAreNotTrusted()
    {
        Assert.Null(TrustedProxyConfiguration.Create(null));
        Assert.Null(TrustedProxyConfiguration.Create(Array.Empty<string>()));
        // A key that is present but empty is a misconfiguration: refuse to start
        // rather than silently ignore the operator's intent.
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Create(new[] { "  " }));
    }

    [Fact]
    public void InvalidEntry_FailsStartupInsteadOfSilentlyTrustingNothing()
    {
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Create(new[] { "not-an-ip" }));
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Create(new[] { "10.0.0.0/99" }));
    }

    [Theory]
    [InlineData("10.1.2.3", "1.2.3.4, 203.0.113.55", "203.0.113.55")]
    [InlineData("10.1.2.3", "203.0.113.55", "203.0.113.55")]
    [InlineData("10.1.2.4", "1.2.3.4, 203.0.113.55", "10.1.2.4")]
    public async Task OnlyListedProxyCanSupplyTheClientAddress(string peer, string forwardedFor, string expected)
    {
        await using var app = await CreateServerAsync(peer, new[] { "10.1.2.3" });
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CidrEntryIsAccepted()
    {
        await using var app = await CreateServerAsync("10.9.8.7", new[] { "10.0.0.0/8" });
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7");
        using var response = await client.SendAsync(request);
        Assert.Equal("198.51.100.7", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> CreateServerAsync(string peer, string[] trustedProxies)
    {
        var options = TrustedProxyConfiguration.Create(trustedProxies)!;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        // Stand in for the socket layer: TestServer leaves the address null.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            await next();
        });
        app.UseForwardedHeaders(options);
        app.Run(context => context.Response.WriteAsync(context.Connection.RemoteIpAddress!.ToString()));
        await app.StartAsync();
        return app;
    }
}
