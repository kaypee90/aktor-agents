using System.Net;
using System.Net.Sockets;

namespace AgentRuntime.Infrastructure.Tools;

/// <summary>
/// Primary handler for agent-initiated outbound HTTP (CLAUDE.md section 44: no unrestricted network
/// access). Every connection — including ones made while following redirects — is checked against
/// the resolved IP address, so agents cannot reach loopback (e.g. this API's own /api/admin/reset),
/// the docker-compose network (Postgres), link-local cloud metadata, or other private ranges.
/// </summary>
internal static class PublicNetworkHandler
{
    public static SocketsHttpHandler Create() => new()
    {
        // With a proxy the callback would validate the proxy's address, not the real target.
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var target = addresses.FirstOrDefault(IsPublic)
                         ?? throw new HttpRequestException(
                             $"Outbound request to '{context.DnsEndPoint.Host}' blocked: it does not resolve to a public address.");

            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            var first = address.GetAddressBytes()[0];
            return (first & 0xFE) != 0xFC; // fc00::/7 unique local
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 10
                 || b[0] == 0
                 || b[0] == 127
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT
                 || (b[0] == 169 && b[1] == 254)               // link-local / cloud metadata
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || b[0] >= 224);                              // multicast / reserved
    }
}
