// Copyright ©2026 Scott Blomfield

using System.Net;
using System.Net.Sockets;

namespace RustArchon.Api.Infrastructure.ThemeUpdates;

/// <summary>
/// Decides whether a resolved IP address is safe for <see cref="ThemeUpdateCheckClient"/> to connect
/// to. The one and only thing this class exists to stop: a theme package's <c>UpdateUrl</c> - untrusted,
/// possibly third-party-authored content, not something the admin who uploaded it necessarily wrote
/// themselves - pointing this Api's own outbound fetch at something on the deployment's internal
/// network (a Postgres/Valkey/Garage admin port, another container's internal API, a cloud metadata
/// endpoint) rather than the theme author's own public update feed. This is a textbook SSRF surface, and
/// this is the fail-closed address check that closes it.
/// </summary>
/// <remarks>
/// Deliberately a denylist of the address ranges that are never a legitimate public update-feed target,
/// not an allowlist of "known-good" ranges - a genuinely public IP a future registry doesn't recognize
/// yet should still work. Every range below is a real SSRF target documented in the wild (169.254.169.254
/// alone is the cloud-metadata endpoint on every major cloud provider), so this list is intentionally
/// conservative rather than minimal.
/// </remarks>
/// <remarks>
/// Public (not internal), specifically so this decision function can be unit-tested directly - the same
/// reasoning <see cref="Administration.ThemePackageValidator"/> itself gives for being <c>public</c>
/// despite living entirely inside <c>RustArchon.Api</c>: there's nothing sensitive in the logic itself,
/// only in getting it wrong.
/// </remarks>
public static class SafeHttpAddressPolicy
{
    /// <summary>
    /// <c>true</c> if <paramref name="address"/> must never be connected to by the update-check client.
    /// Fails closed: an address family this method doesn't specifically recognize as safe is treated as
    /// disallowed, not allowed.
    /// </summary>
    public static bool IsDisallowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsDisallowedIPv4(address),
            AddressFamily.InterNetworkV6 => IsDisallowedIPv6(address),
            // An address family this policy has no specific rule for (e.g. AddressFamily.Unix) is not
            // something a real DNS answer for an http(s) update URL should ever produce - fail closed
            // rather than assume it's fine.
            _ => true
        };
    }

    private static bool IsDisallowedIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();

        return b[0] switch
        {
            0 => true, // 0.0.0.0/8 - "this network"
            10 => true, // 10.0.0.0/8 - RFC 1918 private
            100 when b[1] is >= 64 and <= 127 => true, // 100.64.0.0/10 - carrier-grade NAT
            127 => true, // 127.0.0.0/8 - loopback (IsLoopback above already covers this; kept explicit)
            169 when b[1] == 254 => true, // 169.254.0.0/16 - link-local, includes cloud metadata (169.254.169.254)
            172 when b[1] is >= 16 and <= 31 => true, // 172.16.0.0/12 - RFC 1918 private
            192 when b[1] == 0 && b[2] == 0 => true, // 192.0.0.0/24 - IETF protocol assignments
            192 when b[1] == 168 => true, // 192.168.0.0/16 - RFC 1918 private
            198 when b[1] is 18 or 19 => true, // 198.18.0.0/15 - benchmarking
            >= 224 => true, // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast
            _ => false
        };
    }

    private static bool IsDisallowedIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        // fc00::/7 - unique local addresses, IPv6's equivalent of RFC 1918 private space.
        var firstByte = address.GetAddressBytes()[0];
        return (firstByte & 0xFE) == 0xFC;
    }
}
