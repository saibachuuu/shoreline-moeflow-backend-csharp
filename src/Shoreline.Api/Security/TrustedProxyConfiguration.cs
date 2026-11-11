using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Shoreline.Api.Security;

/// <summary>
/// Opt-in, fail-closed <c>X-Forwarded-*</c> support. Forwarded headers are only
/// honoured when the operator lists the exact reverse proxy addresses or
/// networks that terminate client connections
/// (<c>Security:TrustedProxies:0</c>, <c>:1</c>, ...). With no configuration
/// the middleware is not installed at all and the connection address is used,
/// so a spoofed header can never change the rate-limit identity.
/// </summary>
public static class TrustedProxyConfiguration
{
    public const string SectionName = "Security:TrustedProxies";

    /// <summary>
    /// Build the middleware options, or <c>null</c> when no proxies are listed.
    /// Invalid entries fail startup instead of silently trusting or ignoring a
    /// header; the framework defaults (loopback) are removed so a deployment
    /// must state its own trusted proxy explicitly.
    /// </summary>
    public static ForwardedHeadersOptions? Create(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count == 0) return null;
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // nginx appends the immediate peer address, so the rightmost entry is
            // the only one a single trusted proxy can vouch for.
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            var parts = entry.Split('/');
            if (parts.Length == 2)
            {
                var network = Parse(parts[0], entry);
                var maxPrefix = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > maxPrefix)
                    throw new InvalidOperationException($"可信代理网段配置无效: {entry}");
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(network, prefix));
                continue;
            }

            options.KnownProxies.Add(Parse(entry, entry));
        }

        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            throw new InvalidOperationException("可信代理配置不能为空; 未配置时不要设置该键");
        return options;
    }

    private static IPAddress Parse(string value, string entry) =>
        IPAddress.TryParse(value.Trim(), out var address)
            ? address
            : throw new InvalidOperationException($"可信代理地址配置无效: {entry}");
}
