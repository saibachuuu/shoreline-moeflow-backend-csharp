using System.Net;
using System.Net.Sockets;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Connection-time public-address guard (DNS rebinding defence).
/// Configuration-time checks (HTTPS scheme, allowlists) only validate the
/// configured hostname string. This guard re-validates the actual addresses
/// returned by DNS for every new connection and refuses to open a socket to
/// non-public targets, so a configured private HTTPS destination (or a
/// rebinding hostname) can never receive credentials such as a Bearer API key.
/// </summary>
public static class PublicNetworkGuard
{
    private static readonly (IPAddress Network, int Prefix)[] NonGlobalV4 =
        new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24",
            "203.0.113.0/24", "224.0.0.0/3" }
        .Select(text =>
        {
            var parts = text.Split('/');
            return (IPAddress.Parse(parts[0]), int.Parse(parts[1]));
        }).ToArray();

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (address.Equals(IPAddress.Parse("192.0.0.9")) || address.Equals(IPAddress.Parse("192.0.0.10")))
                return true;
            return !NonGlobalV4.Any(range => InSubnet(address, range.Network, range.Prefix));
        }
        var bytes = address.GetAddressBytes();
        return (bytes[0] & 0xe0) == 0x20
            && !InSubnet(address, IPAddress.Parse("2001::"), 23)
            && !InSubnet(address, IPAddress.Parse("2001:db8::"), 32)
            && !InSubnet(address, IPAddress.Parse("2002::"), 16);
    }

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> implementation. The
    /// <paramref name="hostLabel"/> only shapes the failure text (for example
    /// "归档主机" or "合作方主机"); filtering semantics are identical for all callers.
    /// </summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct) =>
        ConnectAsync(context, ct, "远程主机");

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct, string hostLabel)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var publicAddresses = addresses.Where(IsPublic).ToArray();
        if (publicAddresses.Length == 0)
            throw new HttpRequestException(hostLabel + "没有可用的公网地址");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Exception? last = null;
        foreach (var address in publicAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), timeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                last = error;
                socket.Dispose();
                if (timeout.IsCancellationRequested) break;
            }
        }
        throw new HttpRequestException(hostLabel + "连接失败", last);
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
