using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace Shoreline.Api.Security;

public static class ExternalApiUrl
{
    private static readonly (IPAddress Network, int Prefix)[] NonGlobalV4 =
        new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3" }
        .Select(text => { var parts = text.Split('/'); return (IPAddress.Parse(parts[0]), int.Parse(parts[1])); }).ToArray();

    // This validates a stored URL only. Outbound clients must also validate DNS
    // results at connection time and must not forward credentials on redirects.
    // Reads the archive provider allowlist: appsettings
    // <c>Archive:AllowedHosts</c> wins; otherwise the legacy
    // <c>ARCHIVE_PROVIDER_API_ALLOWED_HOSTS</c> env var (comma-separated) is
    // honored, mirroring Python's provider_api_allowed_hosts(). Entry
    // normalization matches normalize_external_api_url(): strip, lowercase,
    // drop trailing dots; empties are ignored at match time.
    public static IReadOnlyList<string> ReadArchiveAllowedHosts(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Archive:AllowedHosts").Get<string[]>();
        if (configured is { Length: > 0 }) return configured;
        var legacy = configuration["ARCHIVE_PROVIDER_API_ALLOWED_HOSTS"];
        if (string.IsNullOrWhiteSpace(legacy)) return Array.Empty<string>();
        return legacy.Split(',')
            .Select(item => item.Trim().ToLowerInvariant().TrimEnd('.'))
            .Where(item => item.Length != 0)
            .ToArray();
    }

    public static string Normalize(string value, IReadOnlyList<string> allowedHosts, bool requireAllowlist = true)
    {
        value = value.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("归档 API 地址必须使用 HTTPS");
        if (!uri.IsWellFormedOriginalString() || value.Contains('\\') || value.Any(char.IsControl) || uri.UserInfo.Length != 0)
            throw new ArgumentException("归档 API 地址无效");
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("归档 API 地址不能包含 query 或 fragment");
        var host = uri.IdnHost.TrimEnd('.').Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
        {
            if (!IsGlobal(ip)) throw new ArgumentException("归档 API 地址不能指向私有或保留 IP");
        }
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("localhost.localdomain", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("归档 API 地址不能指向本地或内部主机");
        var allowed = allowedHosts.Select(item => item.Trim().TrimStart('*', '.').TrimEnd('.'))
            .Where(item => item.Length != 0).Any(item => host.Equals(item, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + item, StringComparison.OrdinalIgnoreCase));
        if (requireAllowlist && !allowed) throw new ArgumentException("团队归档 API 地址不在站点允许的主机白名单中");
        return value.TrimEnd('/');
    }

    private static bool IsGlobal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) return IsGlobal(ip.MapToIPv4());
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // IANA anycast exceptions in the otherwise non-global 192.0.0/24.
            if (ip.Equals(IPAddress.Parse("192.0.0.9")) || ip.Equals(IPAddress.Parse("192.0.0.10"))) return true;
            return !NonGlobalV4.Any(range => InSubnet(ip, range.Network, range.Prefix));
        }
        var bytes = ip.GetAddressBytes();
        return (bytes[0] & 0xe0) == 0x20
            && !InSubnet(ip, IPAddress.Parse("2001::"), 23)
            && !InSubnet(ip, IPAddress.Parse("2001:db8::"), 32)
            && !InSubnet(ip, IPAddress.Parse("2002::"), 16);
    }

    private static bool InSubnet(IPAddress address, IPAddress network, int prefix)
    {
        var left = address.GetAddressBytes();
        var right = network.GetAddressBytes();
        for (var i = 0; i < prefix / 8; i++) if (left[i] != right[i]) return false;
        var rest = prefix % 8;
        return rest == 0 || ((left[prefix / 8] ^ right[prefix / 8]) & (0xff << (8 - rest))) == 0;
    }
}
