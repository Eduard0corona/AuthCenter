using System.Net;
using System.Net.Sockets;
namespace AuthCenter.Infrastructure.Security;

internal static class OutboundUrlSafety
{
    public static async Task<bool> IsPublicHttpsAsync(string? value, CancellationToken ct)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsDefaultPort is false && uri.Port is not 443 || !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType == UriHostNameType.Unknown) return false;
        IPAddress[] addresses; try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); } catch (SocketException) { return false; }
        return addresses.Length > 0 && addresses.All(IsPublic);
    }
    private static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4(); var bytes = address.GetAddressBytes();
        if (bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 0 || bytes[0] >= 224 || (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127))) return false;
        return true;
    }
}
