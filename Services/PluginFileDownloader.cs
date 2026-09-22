// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Services;

/// <summary>What fetching a plugin file came to.</summary>
/// <param name="Content">The bytes, when <see cref="Ok"/>. Held in memory to be hashed and looked at, then dropped - never stored.</param>
/// <param name="RetryAfter">How long the host asked to be left alone, when it said so.</param>
public sealed record PluginFileDownload(bool Ok, byte[]? Content, string Reason, TimeSpan? RetryAfter = null, bool TooLarge = false)
{
    public static PluginFileDownload Success(byte[] content) => new(true, content, "downloaded");

    public static PluginFileDownload Failure(string reason, TimeSpan? retryAfter = null) => new(false, null, reason, retryAfter);

    /// <summary>The host is serving more than the platform will take in. Not a failure to try again: the file is what it is.</summary>
    public static PluginFileDownload ExceedsLimit(long limit) => new(false, null, $"larger than the {limit / (1024 * 1024)} MB the platform will download", null, TooLarge: true);
}

/// <summary>Fetches the plugin file at an address that has already been checked (https, on the marketplace's own host).</summary>
public interface IPluginFileDownloader
{
    /// <summary>Fetches once. Never throws for anything the network or the host does; that is a failed <see cref="PluginFileDownload"/>.</summary>
    Task<PluginFileDownload> DownloadAsync(Uri address, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The address is one the platform decided to trust when it was stored, but a marketplace may send a download on to a file host of its own, so
/// redirects are followed - by hand, a few hops at most, and only to https. Where they lead is not trusted: every connection, first or after a
/// redirect, is refused unless the name resolves to a public internet address (see <see cref="IsPublicAddress"/>), so a redirect - or a name that
/// starts to resolve somewhere else - can never turn this into a way to reach this network's own machines.
/// </para>
/// <para>
/// <b>The size is bounded, and never taken from the host's word.</b> Scott's rule is that a file an authorized distributor serves is taken whatever its
/// size, and the bound is generous (100 MB by default; real plugins are kilobytes to a few megabytes) - but the whole file is held in memory to be
/// hashed and looked at, so an unbounded read lets any host that streams for the length of the timeout consume the Api's memory. A
/// <c>Content-Length</c> over the bound refuses at once; and because that header can be missing (chunked) or false, the body is also read through a
/// counter that stops the moment the bound is passed. The bound is a Platform Setting and can never exceed what a game server's plugin would be told to
/// fetch (<see cref="RustArchon.Shared.PluginZips.ZipMapping.MaxArchiveBytes"/>).
/// </para>
/// </remarks>
public class PluginFileDownloader(HttpClient http, ILogger<PluginFileDownloader> logger, IPlatformSettingsCache? settings = null) : IPluginFileDownloader
{
    /// <summary>The default largest download, in bytes.</summary>
    public const long DefaultMaxBytes = 100L * 1024 * 1024;

    /// <summary>The largest download in force: the Platform Setting (megabytes), or the default, and never more than a game server would be asked to fetch.</summary>
    private async Task<long> MaxBytesAsync()
    {
        var configured = settings is null ? null : await settings.GetStringAsync(PlatformSettingsRegistry.PluginFileMaxMegabytes);
        var bytes = int.TryParse(configured, out var megabytes) && megabytes > 0 ? megabytes * 1024L * 1024 : DefaultMaxBytes;
        return Math.Min(bytes, RustArchon.Shared.PluginZips.ZipMapping.MaxArchiveBytes);
    }

    /// <summary>Redirects followed before giving up.</summary>
    public const int MaxRedirects = 5;

    /// <summary>How long the whole download may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The longest a host's "leave me alone for" is honoured.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(6);

    public async Task<PluginFileDownload> DownloadAsync(Uri address, CancellationToken callerToken)
    {
        // One clock for the whole download, headers and body: the HttpClient's own timeout stops at the headers when they are read first.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        limit.CancelAfter(Timeout);
        var cancellationToken = limit.Token;

        try
        {
            var current = address;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (current.Scheme != Uri.UriSchemeHttps)
                {
                    return PluginFileDownload.Failure("the download would not be fetched over anything but https");
                }

                using var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (IsRedirect(response.StatusCode))
                {
                    var next = response.Headers.Location;
                    if (next is null)
                    {
                        return PluginFileDownload.Failure($"the host answered {(int)response.StatusCode} without saying where");
                    }

                    current = next.IsAbsoluteUri ? next : new Uri(current, next);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return PluginFileDownload.Failure("the host said it is being asked too often", RetryAfter(response));
                }

                if (!response.IsSuccessStatusCode)
                {
                    return PluginFileDownload.Failure($"the host answered {(int)response.StatusCode}");
                }

                var max = await MaxBytesAsync();
                if (response.Content.Headers.ContentLength is { } declared && declared > max)
                {
                    return PluginFileDownload.ExceedsLimit(max);
                }

                // Through a counter, whatever the header said: it can be absent (chunked) or false.
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > max)
                    {
                        return PluginFileDownload.ExceedsLimit(max);
                    }

                    buffer.Write(chunk, 0, read);
                }

                return PluginFileDownload.Success(buffer.ToArray());
            }

            return PluginFileDownload.Failure("too many redirects");
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return PluginFileDownload.Failure("the host did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation(ex, "A plugin file could not be fetched.");
            return PluginFileDownload.Failure("the host could not be reached (or is not a public internet address)");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        var wait = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } w && w > TimeSpan.Zero ? (w > MaxRetryAfter ? MaxRetryAfter : w) : null;
    }

    /// <summary>The handler the download client must use: no automatic redirects, and connections only to public internet addresses.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var allowed = addresses.Where(IsPublicAddress).ToArray();
            if (allowed.Length == 0)
            {
                throw new HttpRequestException("the name does not resolve to a public internet address");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    /// <summary>
    /// Whether an address is on the public internet: not this machine, not a private or shared range, not link-local (which is where cloud
    /// metadata services live), not multicast, not reserved. An IPv4 address written as IPv6 is judged as the IPv4 address it is.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0                                              // "this network"
                || bytes[0] == 10                                               // private
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)       // shared address space (carrier-grade NAT)
                || (bytes[0] == 169 && bytes[1] == 254)                         // link-local, including cloud metadata
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)        // private
                || (bytes[0] == 192 && bytes[1] == 168)                         // private
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)          // protocol assignments
                || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))      // benchmarking
                || bytes[0] >= 224);                                            // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (bytes[0] & 0xFE) == 0xFC);                                  // unique local (fc00::/7)
        }

        return false;
    }
}
