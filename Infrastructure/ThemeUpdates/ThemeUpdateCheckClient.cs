// Copyright ©2026 Scott Blomfield

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using RustArchon.Api.Administration;

namespace RustArchon.Api.Infrastructure.ThemeUpdates;

/// <summary>
/// Thrown from <see cref="ThemeUpdateCheckClient.ConnectToValidatedAddressAsync"/> to abort a connection
/// attempt <see cref="SafeHttpAddressPolicy"/> refused. <see cref="SocketsHttpHandler"/> wraps whatever a
/// <c>ConnectCallback</c> throws in an <see cref="HttpRequestException"/>, so this exception's own
/// <see cref="Exception.Message"/> is exactly what <see cref="ThemeUpdateCheckClient"/> surfaces to the
/// caller - kept to the same small, fixed vocabulary as every other failure path here.
/// </summary>
internal sealed class ThemeUpdateBlockedException(string message) : Exception(message);

/// <summary>
/// The only place this Api makes an outbound HTTP request to a URL it didn't choose itself - a theme
/// package's own <c>manifest.json</c> can set <c>UpdateUrl</c> to anything, and a theme package is
/// exactly the kind of thing that gets shared and installed from someone other than the admin who
/// eventually uploads it. That combination (untrusted target, server-initiated fetch - and, for
/// <see cref="DownloadPackageAsync"/>, a server-initiated fetch of a whole zip that then gets installed)
/// is the textbook shape of Server-Side Request Forgery, so every piece of this class exists to answer
/// one question specifically: "if <c>UpdateUrl</c> (or the package location it reports) pointed at this
/// deployment's own internal network instead of a real update feed, what would go wrong?" - and to make
/// the answer "nothing."
/// </summary>
/// <remarks>
/// <para>
/// <strong>Address validation, not just a well-formed-URL check.</strong> <c>ThemePackageValidator</c>
/// already confirms <c>UpdateUrl</c> is a well-formed absolute <c>http</c>/<c>https</c> URL at upload
/// time, but that says nothing about where the hostname actually resolves - a hostname is just as
/// capable of resolving to <c>127.0.0.1</c>, a container's own internal IP, or the cloud metadata address
/// <c>169.254.169.254</c> as to a real public server. This class resolves the hostname itself and
/// refuses to connect if any resolved address is private, loopback, link-local, or otherwise
/// non-public - see <see cref="SafeHttpAddressPolicy"/>.
/// </para>
/// <para>
/// <strong>Connects to the address it validated, not a hostname it hopes resolves the same way twice.</strong>
/// Validating a hostname's DNS answer and then simply handing that same hostname to
/// <see cref="HttpClient"/> would leave a classic DNS-rebinding gap open: nothing stops the name
/// resolving to a safe address for this check and a completely different (internal) one a moment later
/// when <see cref="HttpClient"/> does its own, separate resolution to actually connect. Instead, this
/// class supplies its own <see cref="SocketsHttpHandler.ConnectCallback"/>
/// (<see cref="ConnectToValidatedAddressAsync"/>), which resolves and validates the address and then
/// opens the TCP connection to that exact validated <see cref="IPAddress"/> itself - there is no second,
/// independent resolution for an attacker's DNS server to answer differently. TLS still validates the
/// certificate against the original hostname as normal, since only the transport-level connection target
/// is pinned, not the request's own <c>Host</c>/SNI. Both <see cref="CheckAsync"/> and
/// <see cref="DownloadPackageAsync"/> share this one handler (see <c>Program.cs</c>'s registration), so
/// neither gets this protection by accident and neither can skip it.
/// </para>
/// <para>
/// <strong>https only, redirects refused outright, every response capped, everything time-boxed.</strong>
/// <c>UpdateUrl</c> may be a plain <c>http://</c> URL as far as package validation is concerned (it's
/// only ever displayed there, never fetched), but a real outbound request is held to a tighter rule
/// here: no cleartext round trip, for either the version check or the package download. Redirects are
/// never followed (<c>AllowAutoRedirect = false</c>) rather than re-validated per hop, which would just
/// be more surface for the same class of bug. The version-check response is capped at
/// <see cref="MaxCheckResponseBytes"/> and the package download at <see cref="MaxPackageBytes"/> (the
/// same limit <c>ThemesController</c>'s own upload endpoint enforces) - neither trusts
/// <c>Content-Length</c> alone, same "don't trust the declared size" reasoning as
/// <c>ThemePackageValidator.CopyWithLimit</c>. Each call is bounded by its own timeout, sized for what it
/// actually has to transfer - <see cref="CheckTimeout"/> for a handful of JSON bytes,
/// <see cref="DownloadTimeout"/> for up to <see cref="MaxPackageBytes"/> of package.
/// </para>
/// <para>
/// <strong>Every failure becomes one of a small set of fixed strings, never the raw exception or any
/// part of the remote response.</strong> <see cref="Data.Theme.LastUpdateCheckError"/> is shown directly
/// to a Site Admin in the Panel - if a failure message could carry attacker-influenced text (an internal
/// service's own error page, an exception's stack trace, a response body), a malicious <c>UpdateUrl</c>
/// could use it as a one-way channel to exfiltrate whatever that internal probe turned up, entirely
/// defeating the address validation above. So nothing caught here is ever passed through verbatim; every
/// branch below maps to a message this class wrote itself. The package itself, once downloaded, is never
/// trusted either - it goes through the exact same <c>ThemePackageValidator</c> a manual admin upload
/// does (see <c>ThemeService.InstallUpdateAsync</c>), never installed on the strength of having come
/// from a URL the theme's own manifest named.
/// </para>
/// </remarks>
public sealed class ThemeUpdateCheckClient(HttpClient httpClient) : IThemeUpdateCheckClient
{
    /// <summary>An update manifest is a handful of short fields - anything past this is either not a
    /// real update feed or itself a (small-scale) resource-exhaustion attempt, and either way isn't
    /// something worth reading further.</summary>
    private const int MaxCheckResponseBytes = 64 * 1024;

    /// <summary>Matches <c>ThemesController.Upload</c>'s own <c>[RequestSizeLimit]</c> - an installed
    /// package is held to the exact same ceiling as a manually uploaded one.</summary>
    private const long MaxPackageBytes = 25 * 1024 * 1024;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The handler <c>Program.cs</c> registers this client with - see this class's own remarks for why a
    /// plain <see cref="HttpClient"/> isn't safe to point at a package-supplied URL.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = ConnectTimeout,
        ConnectCallback = ConnectToValidatedAddressAsync
    };

    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/> that does the actual SSRF-safe connect - see
    /// this class's own remarks for the DNS-rebinding reasoning behind connecting to the address this
    /// method itself resolved and validated, rather than a hostname handed back to the framework's own
    /// (separate) resolution.
    /// </summary>
    private static async ValueTask<Stream> ConnectToValidatedAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new ThemeUpdateBlockedException("Could not resolve the URL's host.");
        }

        // Fail closed on the whole answer, not just the addresses that happen to be disallowed - a
        // hostname that resolves to both a public and a private address is refused entirely rather than
        // just steered toward the "safe-looking" one, since nothing here can prove which address a
        // differently-timed request would actually receive.
        if (addresses.Length == 0 || Array.Exists(addresses, SafeHttpAddressPolicy.IsDisallowed))
        {
            throw new ThemeUpdateBlockedException("The URL points to an address that isn't allowed.");
        }

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
            }
        }

        throw new ThemeUpdateBlockedException("Could not connect to the URL.");
    }

    public async Task<ThemeUpdateCheckOutcome> CheckAsync(string updateUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ThemeUpdateCheckOutcome.Failed("The update URL must use https to be checked automatically.");
        }

        var fetch = await FetchAsync(uri, "application/json", MaxCheckResponseBytes, CheckTimeout, cancellationToken);
        if (!fetch.Success)
        {
            return ThemeUpdateCheckOutcome.Failed(fetch.Error!);
        }

        RemoteUpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RemoteUpdateManifest>(fetch.Content!, JsonOptions);
        }
        catch (JsonException)
        {
            return ThemeUpdateCheckOutcome.Failed("The update URL didn't return valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(manifest?.Version) || !ThemeVersionComparer.LooksLikeVersion(manifest.Version))
        {
            return ThemeUpdateCheckOutcome.Failed("The update URL's response didn't include a recognizable version.");
        }

        // The package location is only ever displayed (as a version number) or fed straight back into
        // this same client's own DownloadPackageAsync (which re-validates the scheme itself before
        // connecting) - a malformed or missing one just means "no one-click install," never a reason to
        // fail the whole check, since the version information alone is still useful to report.
        var packageUrl = manifest.PackageUrl is { Length: > 0 and <= 2000 } candidate
            && Uri.TryCreate(candidate, UriKind.Absolute, out var packageUri)
            && (packageUri.Scheme == Uri.UriSchemeHttp || packageUri.Scheme == Uri.UriSchemeHttps)
                ? candidate.Trim()
                : null;

        return ThemeUpdateCheckOutcome.Succeeded(manifest.Version.Trim(), packageUrl);
    }

    public async Task<ThemePackageDownloadOutcome> DownloadPackageAsync(string packageUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ThemePackageDownloadOutcome.Failed("The package URL must use https.");
        }

        var fetch = await FetchAsync(uri, "application/zip", MaxPackageBytes, DownloadTimeout, cancellationToken);
        return fetch.Success
            ? ThemePackageDownloadOutcome.Succeeded(fetch.Content!)
            : ThemePackageDownloadOutcome.Failed(fetch.Error!);
    }

    /// <summary>The one place either public method actually sends a request - everything above this is
    /// URL/shape validation, and everything below it is response-shape validation specific to the
    /// caller.</summary>
    private async Task<(bool Success, byte[]? Content, string? Error)> FetchAsync(
        Uri uri, string acceptContentType, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptContentType));
            // Identifies the request as coming from this feature specifically, and gives the theme
            // author's own server something to see in its logs beyond an anonymous GET - courteous, and
            // also avoids the (fairly common) server-side rule that blocks a request with no UA at all.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RustArchon-ThemeUpdateCheck", "1"));

            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                // The numeric status code alone, nothing from the response body - see this class's own
                // remarks on why nothing attacker-influenced reaches a caller's error message.
                return (false, null, $"The URL returned HTTP {(int)response.StatusCode}.");
            }

            var body = await ReadWithLimitAsync(response.Content, maxBytes, linkedCts.Token);
            return body is null
                ? (false, null, "The response was too large.")
                : (true, body, null);
        }
        catch (HttpRequestException ex)
        {
            return (false, null, ex.InnerException is ThemeUpdateBlockedException blocked
                ? blocked.Message
                : "Could not reach the URL.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return (false, null, "The request took too long.");
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> from <paramref name="content"/>, returning
    /// <c>null</c> the moment that limit would be exceeded - never trusts <c>Content-Length</c> alone,
    /// same reasoning as <c>ThemePackageValidator.CopyWithLimit</c>.</summary>
    private static async Task<byte[]?> ReadWithLimitAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The literal JSON shape a theme author's own update feed is expected to return - see this
    /// class's remarks on why every field here is treated as untrusted regardless.</summary>
    private sealed record RemoteUpdateManifest(string? Version, string? PackageUrl);
}
