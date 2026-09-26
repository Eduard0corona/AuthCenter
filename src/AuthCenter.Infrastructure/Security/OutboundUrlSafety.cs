using System.Net;
using System.Net.Sockets;
namespace AuthCenter.Infrastructure.Security;

/// <summary>
/// Keeps administrator-configured outbound URLs (event hooks) on the public internet: HTTPS on port
/// 443 to addresses that are not private, loopback, link-local, multicast, shared, reserved or an
/// IPv6 form of one of those.
/// </summary>
internal static class OutboundUrlSafety
{
    public static async Task<bool> IsPublicHttpsAsync(string? value, CancellationToken ct)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsDefaultPort is false && uri.Port is not 443 || !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType == UriHostNameType.Unknown) return false;
        IPAddress[] addresses; try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); } catch (SocketException) { return false; }
        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    /// <summary>
    /// The connection step of the outbound HTTP handler: it resolves the host again and dials only
    /// public addresses, so a DNS answer that changes after <see cref="IsPublicHttpsAsync"/> (DNS
    /// rebinding) cannot reach an internal host.
    /// </summary>
    public static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || !addresses.All(IsPublic))
            throw new HttpRequestException($"The host {context.DnsEndPoint.Host} does not resolve only to public addresses.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return IsPublicIPv4(bytes);

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        if ((bytes[0] & 0xFE) == 0xFC) return false;                                   // fc00::/7 unique local
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false; // 2001:db8::/32 documentation
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) return false; // 2001::/32 Teredo
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return IsPublicIPv4(bytes[2..6]);    // 2002::/16 6to4 embeds an IPv4 address
        // 64:ff9b::/96 and 64:ff9b:1::/48 translate to IPv4 (NAT64): judge the embedded address.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B) return IsPublicIPv4(bytes[12..16]);
        return !bytes.Take(12).All(value => value == 0);                               // ::/96, including IPv4-compatible forms
    }

    private static bool IsPublicIPv4(byte[] bytes) => !(
        bytes[0] is 0 or 10 or 127 or >= 224 ||                                        // this network, private, loopback, multicast and reserved
        bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||                            // 100.64.0.0/10 shared address space (CGNAT)
        bytes[0] == 169 && bytes[1] == 254 ||                                          // link-local, including cloud metadata endpoints
        bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
        bytes[0] == 192 && bytes[1] == 168 ||
        bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2 ||                     // IETF protocol assignments, TEST-NET-1
        bytes[0] == 198 && bytes[1] is 18 or 19 ||                                     // benchmarking
        bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||                        // TEST-NET-2
        bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);                          // TEST-NET-3
}
