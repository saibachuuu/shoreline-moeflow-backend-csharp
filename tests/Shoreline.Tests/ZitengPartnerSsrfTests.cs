using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shoreline.Api.Modules.ZitengPartner;

namespace Shoreline.Tests;

/// <summary>
/// SSRF regression coverage for the Ziteng partner client. Requests go through
/// the real DI-registered <see cref="HttpClient"/> primary handler (with the
/// shared public-network connect guard), so a configured private HTTPS
/// destination is rejected before any socket opens and never receives the
/// Bearer API key. No test contacts a real external host: only
/// loopback/private literals are used.
/// </summary>
public sealed class ZitengPartnerSsrfTests
{
    [Theory]
    [InlineData("https://127.0.0.1:9/partner-api/v1/works/search")]
    [InlineData("https://10.255.255.1:9/partner-api/v1/works/search")]
    [InlineData("https://192.168.0.1:9/partner-api/v1/works/search")]
    [InlineData("https://[::1]:9/partner-api/v1/works/search")]
    public async Task RegisteredClient_RejectsPrivateTargetsBeforeSocketOpens(string apiUrl)
    {
        using var provider = BuildProvider(apiUrl);
        var client = provider.GetRequiredService<ZitengPartnerClient>();

        var error = await Assert.ThrowsAsync<ZitengPartnerApiException>(() => client.SearchAsync("作品"));

        Assert.Equal("请求失败: 网络错误", error.Message);
        // 10/8 without the guard would hang until the connect timeout and
        // 127.0.0.1:9 would refuse fast; the guard message proves rejection
        // happened up front, before any socket opened.
        Assert.Contains("公网地址", Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredClient_KeepsHttpsConfigValidation()
    {
        // The connect guard is a second layer: plaintext and credential-bearing
        // URLs are still rejected at configuration time.
        Assert.Throws<InvalidOperationException>(() => ZitengPartnerSettings.FromConfiguration(
            Configuration(("ZITENG_PARTNER_API_URL", "http://127.0.0.1:9/search"))));
        Assert.Throws<InvalidOperationException>(() => ZitengPartnerSettings.FromConfiguration(
            Configuration(("ZITENG_PARTNER_API_URL", "https://user:pass@127.0.0.1:9/search"))));
    }

    private static ServiceProvider BuildProvider(string apiUrl)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration(
            ("ZITENG_PARTNER_API_URL", apiUrl),
            ("ZITENG_PARTNER_API_KEY", "ssrf-test-key")));
        services.AddSingleton(TimeProvider.System);
        services.AddZitengPartner();
        // The Mongo-backed rate-limit gate is irrelevant to transport SSRF:
        // stub it so this test needs no database while the real HttpClient
        // primary handler registration stays wired.
        services.RemoveAll<IZitengPartnerApiGate>();
        services.AddSingleton<IZitengPartnerApiGate, ImmediateGate>();
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(item => item.Key, item => item.Value)).Build();

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (var current = error; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        return string.Join(" | ", messages);
    }

    private sealed class ImmediateGate : IZitengPartnerApiGate
    {
        public Task WaitAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
