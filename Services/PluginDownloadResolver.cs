// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Services;

/// <summary>What the marketplace index answered for one plugin, and what was made of it.</summary>
/// <param name="AskedToWait">Set when the index said it is being asked too often (429): how long to leave it alone, for everyone.</param>
public sealed record PluginDownloadAnswer(
    PluginDownloadMatch Match, int? HttpStatus, string? ResponseJson, bool ResponseTruncated, TimeSpan? AskedToWait);

/// <summary>Asks the marketplace index whether a plugin has a direct download address.</summary>
public interface IPluginDownloadResolver
{
    /// <summary>Asks once. Never throws for anything the network or the index does; that is a <see cref="PluginDownloadOutcome.Failed"/> answer.</summary>
    Task<PluginDownloadAnswer> AskAsync(PluginDownloadRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Only <see cref="BaseAddress"/> is ever called, never an address from a request or from the index's own answer, so this cannot be turned
/// into a way to make the Api fetch arbitrary addresses. What is sent is a plugin's name and its marketplace - public information about a
/// public listing - and nothing about any server, organization or player. The index needs no credentials and none are sent.
/// </para>
/// <para>
/// The index is a third party's, undocumented, and free to change or disappear, so every way it can go wrong is a quiet
/// <see cref="PluginDownloadOutcome.Failed"/>: no link is shown and the update notice's own marketplace page still is.
/// </para>
/// </remarks>
public class PluginDownloadResolver(HttpClient http, ILogger<PluginDownloadResolver> logger) : IPluginDownloadResolver
{
    /// <summary>The one place this ever asks.</summary>
    public const string BaseAddress = "https://serverarmour.com/";

    public const string SearchPath = "api/v1/marketplace/search";

    /// <summary>How long to leave the index alone when it says "too often" without saying for how long.</summary>
    public static readonly TimeSpan DefaultBackOff = TimeSpan.FromMinutes(30);

    /// <summary>The longest a back-off is honoured, whatever the index asks for.</summary>
    public static readonly TimeSpan MaxBackOff = TimeSpan.FromHours(6);

    public async Task<PluginDownloadAnswer> AskAsync(PluginDownloadRequest request, CancellationToken cancellationToken)
    {
        // The marketplace filter is the index's own: it narrows the answer to that marketplace's listings, which is most of the difference
        // between a few hundred bytes and a hundred listings.
        var path = $"{SearchPath}?plugin={Uri.EscapeDataString(request.Name)}&market={Uri.EscapeDataString(request.Marketplace)}";

        try
        {
            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("The marketplace index says it is being asked too often; leaving it alone for a while.");
                return Fail("the index said it is being asked too often", status, BackOff(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail($"the index answered {status}", status);
            }

            var (text, truncated) = await ReadCappedAsync(response.Content, cancellationToken);
            if (truncated)
            {
                return new PluginDownloadAnswer(
                    new PluginDownloadMatch(PluginDownloadOutcome.Failed, null, null, null, null, "the answer was too large"), status, text, true, null);
            }

            return new PluginDownloadAnswer(PluginDownloadMatcher.Choose(text, request), status, text, false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("the index did not answer in time", null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation(ex, "The marketplace index could not be reached.");
            return Fail("the index could not be reached", null);
        }
    }

    private static PluginDownloadAnswer Fail(string reason, int? status, TimeSpan? askedToWait = null) =>
        new(new PluginDownloadMatch(PluginDownloadOutcome.Failed, null, null, null, null, reason), status, null, false, askedToWait);

    // Retry-After as seconds, or a date; bounded either way, and the default when it says nothing usable.
    private static TimeSpan BackOff(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        var wait = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : DefaultBackOff);
        return wait <= TimeSpan.Zero ? DefaultBackOff : wait > MaxBackOff ? MaxBackOff : wait;
    }

    // The body, as text, up to the column's limit; over it, what fits and a flag - never the whole of something enormous.
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        using var collected = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var room = PluginDownloadLookup.MaxResponseLength - (int)collected.Length;
            if (read > room)
            {
                collected.Write(buffer, 0, room);
                return (Encoding.UTF8.GetString(collected.ToArray()), true);
            }

            collected.Write(buffer, 0, read);
        }

        return (Encoding.UTF8.GetString(collected.ToArray()), false);
    }
}
