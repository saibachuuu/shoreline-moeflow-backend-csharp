using System.Net;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

/// <summary>
/// Focused coverage for the shared connection-time public-address guard.
/// These tests never touch the network: literals are rejected by the guard
/// before any socket is opened, and classification is pure computation.
/// </summary>
public sealed class PublicNetworkGuardTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("192.0.0.9")]
    [InlineData("192.0.0.10")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    public void IsPublic_AcceptsGlobalAddresses(string text) =>
        Assert.True(PublicNetworkGuard.IsPublic(IPAddress.Parse(text)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void IsPublic_RejectsNonGlobalAddresses(string text) =>
        Assert.False(PublicNetworkGuard.IsPublic(IPAddress.Parse(text)));

    [Theory]
    [InlineData("https://127.0.0.1:9/search")]
    [InlineData("https://10.255.255.1:9/search")]
    [InlineData("https://192.168.0.1:9/search")]
    [InlineData("https://[::1]:9/search")]
    public async Task GuardedHandler_RejectsPrivateTargetsBeforeConnecting(string url)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => PublicNetworkGuard.ConnectAsync(context, ct, "归档主机"),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        // 127.0.0.1:9 would otherwise refuse fast and 10/8 would hang until the
        // connect timeout; the guard message proves rejection happened up front.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead));
        Assert.Contains("公网地址", error.Message, StringComparison.Ordinal);
    }
}
