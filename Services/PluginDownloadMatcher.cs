// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Services;

/// <summary>One thing to ask the marketplace index: a plugin as UpdateChecker named it, on a marketplace, at the version it said is newest.</summary>
/// <param name="PageUrl">The plugin's page as UpdateChecker reported it. Used to tell two listings of the same name apart; never fetched.</param>
public sealed record PluginDownloadRequest(string Name, string Marketplace, string Version, string PageUrl)
{
    public string MarketplaceKey => PluginDownloadMatcher.MarketplaceKey(Marketplace);

    public string NormalizedName => PluginUpdateNoticeRepository.Normalize(Name);

    public string VersionKey => PluginDownloadMatcher.VersionKey(Version);
}

/// <summary>What choosing among the index's listings came to.</summary>
public sealed record PluginDownloadMatch(
    PluginDownloadOutcome Outcome, string? DownloadUrl, string? MatchedName, string? MatchedPageUrl, string? MatchedVersion, string? Reason);

/// <summary>
/// Decides, from the marketplace index's answer, whether a plugin has a direct download address that is safe to show, and which.
/// </summary>
/// <remarks>
/// <para>
/// The index is a search: asked for a name it returns everything vaguely like it, across many marketplaces. Nothing in it is taken on
/// trust. A listing counts only if it is on the <b>same marketplace</b> UpdateChecker named, has the <b>same name</b> (letters and digits,
/// so <c>NpcSpawn</c> and <c>Npc Spawn</c> are one), and is <b>at least the version</b> the update is for - an older listing is a stale
/// answer, not a download for this update. Where two listings still qualify, the one whose page is the page UpdateChecker gave wins.
/// </para>
/// <para>
/// The address itself is the sensitive part: a person will fetch that file and put it on their game server. So it must be <c>https</c>,
/// carry no credentials, and sit on the <b>host that marketplace really uses</b> - a marketplace with no entry below, or an address
/// pointing anywhere else, is never passed on, however the index vouched for it. Fails closed throughout: a missing piece is "no link".
/// </para>
/// </remarks>
public static class PluginDownloadMatcher
{
    // The hosts each marketplace serves its own downloads from. A marketplace not listed has no address passed on until someone has looked
    // at where it really serves files and adds it here on purpose.
    private static readonly Dictionary<string, string[]> HostsByMarketplace = new(StringComparer.Ordinal)
    {
        ["umod"] = ["umod.org"],
        ["codefling"] = ["codefling.com"],
        ["github"] = ["github.com"],
        ["rustworkshop"] = ["rustworkshop.space"],
        ["lonedesign"] = ["lone.design"],
        ["serverarmour"] = ["serverarmour.com"],
        ["myvector"] = ["myvector.xyz"],
        ["skyplugins"] = ["skyplugins.ru"],
        ["roguedepot"] = ["roguedepot.com"],
        ["modpulse"] = ["modpulse.com"]
    };

    private const int MaxAddressLength = 500;

    /// <summary>A marketplace's name as a key: letters and digits, lower case (<c>Lone.Design</c> is <c>lonedesign</c>).</summary>
    public static string MarketplaceKey(string? marketplace)
    {
        var key = new string((marketplace ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key.Length <= 100 ? key : key[..100];
    }

    /// <summary>A version as written, without whitespace or a leading <c>v</c>, cut to the column.</summary>
    public static string VersionKey(string? version)
    {
        var key = (version ?? string.Empty).Trim().TrimStart('v', 'V');
        return key.Length <= 50 ? key : key[..50];
    }

    /// <summary>
    /// The address if it is one that may be passed on for this marketplace, else <c>null</c>: absolute <c>https</c>, no credentials,
    /// no longer than its column, and on (or under) a host the marketplace is known to serve from.
    /// </summary>
    public static string? SafeDownloadUrl(string? url, string marketplaceKey)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxAddressLength || !HostsByMarketplace.TryGetValue(marketplaceKey, out var hosts))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Host.Length == 0)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        return hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)) ? uri.AbsoluteUri : null;
    }

    /// <summary>Whether two page addresses are the same page: same host and path, ignoring case, a trailing slash, the scheme and the query.</summary>
    public static bool SamePage(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.AbsolutePath.TrimEnd('/'), y.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a listing at <paramref name="listed"/> is for this update or a newer one. A version it cannot compare must be written exactly the same.</summary>
    public static bool VersionSuffices(string? listed, string wanted)
    {
        var have = VersionKey(listed);
        return have.Length > 0 && wanted.Length > 0
            && (string.Equals(have, wanted, StringComparison.OrdinalIgnoreCase) || PluginVersions.IsAtLeast(have, wanted));
    }

    /// <summary>Chooses among the listings in <paramref name="json"/> (the index's answer, an array). Never throws for a body it cannot read.</summary>
    public static PluginDownloadMatch Choose(string json, PluginDownloadRequest request)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Failed("the answer was not JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Failed("the answer was not a list");
            }

            var marketplaceKey = request.MarketplaceKey;
            var wantedName = request.NormalizedName;
            var wantedVersion = request.VersionKey;

            var sameListing = new List<Listing>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var listing = Listing.From(element);
                if (MarketplaceKey(listing.Marketplace) == marketplaceKey && PluginUpdateNoticeRepository.Normalize(listing.Name) == wantedName)
                {
                    sameListing.Add(listing);
                }
            }

            if (sameListing.Count == 0)
            {
                return NotFound("no listing with this name on this marketplace");
            }

            var current = sameListing.Where(l => VersionSuffices(l.LatestVersion, wantedVersion)).ToList();
            if (current.Count == 0)
            {
                return NotFound("the listing is older than this update");
            }

            // Listings of the same name that are for this update: the one on the page UpdateChecker named first, then the index's own order.
            var usable = current
                .Select(l => (Listing: l, Url: SafeDownloadUrl(l.DownloadUrl, marketplaceKey)))
                .Where(x => x.Url is not null)
                .OrderByDescending(x => SamePage(x.Listing.Url, request.PageUrl))
                .ToList();

            if (usable.Count == 0)
            {
                return NotFound(current.Any(l => !string.IsNullOrWhiteSpace(l.DownloadUrl))
                    ? "the download address is not one we pass on"
                    : "the listing offers no direct download");
            }

            var chosen = usable[0];
            return new PluginDownloadMatch(
                PluginDownloadOutcome.Found, chosen.Url, Cut(chosen.Listing.Name, 200), Cut(chosen.Listing.Url, 500), Cut(chosen.Listing.LatestVersion, 50), null);
        }
    }

    private static PluginDownloadMatch NotFound(string reason) => new(PluginDownloadOutcome.NotFound, null, null, null, null, reason);

    private static PluginDownloadMatch Failed(string reason) => new(PluginDownloadOutcome.Failed, null, null, null, null, reason);

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];

    // A listing's fields, read leniently: a field that is missing or not text is just empty, so an odd entry is skipped, not an error.
    private readonly record struct Listing(string Name, string Marketplace, string Url, string DownloadUrl, string LatestVersion)
    {
        public static Listing From(JsonElement element) => new(
            Text(element, "name"), Text(element, "marketplace"), Text(element, "url"), Text(element, "downloadUrl"), Text(element, "latestVersion"));

        private static string Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }
}
