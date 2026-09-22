// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// The plugins on a server that UpdateChecker says have a newer version. Gated like reading the server (the plugin list beside it is);
/// what it returns is public marketplace information, not anything about players.
/// </summary>
/// <remarks>
/// A notice is kept as reported and judged when read: it is shown only while the plugin is still installed and its installed version is
/// still older than the newest one reported, so updating a plugin makes its notice disappear without waiting for UpdateChecker.
/// </remarks>
[ApiController]
[Route("api/rustservers/{id:guid}/plugin-updates")]
[Authorize]
[RequirePermission(PermissionCatalog.ServerGet)]
public class ServerPluginUpdatesController(
    IRustServerRepository servers, IPluginUpdateNoticeRepository notices, IServerPluginRepository plugins,
    IPluginDownloadLookupRepository downloads) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<PluginUpdateNoticeDto>>> Get(Guid id)
    {
        if (await servers.GetByIdAsync(id, null) is null)
        {
            return NotFound();
        }

        var held = await notices.GetForServerAsync(id);
        if (held.Count == 0)
        {
            return Ok(new List<PluginUpdateNoticeDto>());
        }

        var installed = (await plugins.GetForServerAsync(id) ?? [])
            .GroupBy(p => PluginUpdateNoticeRepository.Normalize(p.Name))
            .ToDictionary(g => g.Key, g => g.First());

        // The direct download addresses already found for these plugins - by the platform's lookup job, shared by every server, so reading
        // them is one query and never a call to anyone. Only the ones asked for at exactly this notice's version and marketplace count.
        var found = await downloads.GetFoundForNamesAsync(held.Select(n => n.NormalizedName).Distinct().ToList());

        var result = new List<PluginUpdateNoticeDto>();
        foreach (var notice in held)
        {
            if (!installed.TryGetValue(notice.NormalizedName, out var plugin) || !StillOutdated(plugin.Version, notice.LatestVersion))
            {
                continue;
            }

            result.Add(new PluginUpdateNoticeDto
            {
                Name = plugin.Name,
                InstalledVersion = plugin.Version,
                ReportedVersion = notice.CurrentVersion,
                LatestVersion = notice.LatestVersion,
                Url = SafeUrl(notice.Url),
                DownloadUrl = DownloadUrlFor(notice, found),
                Marketplace = notice.Marketplace,
                FirstSeenUtc = notice.FirstSeenUtc,
                LastSeenUtc = notice.LastSeenUtc,
                TimesSeen = notice.TimesSeen
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// The address found for this notice's plugin at its newest version on its marketplace, or empty. Checked again here, not only when it
    /// was stored: what goes to a page is decided by the rules as they are now, so tightening them takes effect on what is already held.
    /// </summary>
    private static string DownloadUrlFor(PluginUpdateNotice notice, IReadOnlyList<PluginDownloadLookup> found)
    {
        var marketplaceKey = PluginDownloadMatcher.MarketplaceKey(notice.Marketplace);
        var version = PluginDownloadMatcher.VersionKey(notice.LatestVersion);
        var row = found.FirstOrDefault(l => l.NormalizedName == notice.NormalizedName && l.MarketplaceKey == marketplaceKey && l.Version == version);
        return PluginDownloadMatcher.SafeDownloadUrl(row?.DownloadUrl, marketplaceKey) ?? string.Empty;
    }

    /// <summary>
    /// True unless the installed version is provably at least the newest one reported. A version this cannot compare (not x.y.z) counts as
    /// outdated unless it is written exactly the same, so an odd version scheme shows a notice rather than hiding a real one.
    /// </summary>
    public static bool StillOutdated(string? installedVersion, string latestVersion)
    {
        var installed = Trim(installedVersion);
        var latest = Trim(latestVersion);
        if (latest.Length == 0)
        {
            return false;      // nothing was said about a newer version
        }

        if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !PluginVersions.IsAtLeast(installed, latest);
    }

    /// <summary>
    /// The address if it is an absolute http or https one, else empty. The text is from another plugin, and a page's address is
    /// something a person will click, so anything else (a script address, a relative path, a file) is never passed on.
    /// </summary>
    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500)
        {
            return string.Empty;
        }

        var text = url.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return string.Empty;
        }

        return uri.AbsoluteUri;
    }

    private static string Trim(string? version) => (version ?? string.Empty).Trim().TrimStart('v', 'V');
}
