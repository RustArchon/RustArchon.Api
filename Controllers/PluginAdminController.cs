// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin management of the RustArchon plugin's signing keys and releases: rotate or revoke a key, and upload,
/// publish or withdraw a plugin file for delivery. Every action is audit-logged (see <see cref="PluginAdminEvent"/>).
/// </summary>
/// <remarks>
/// Gated by the same <c>ManagePlatformSettings</c> permission as the platform settings page (the global Site Admin
/// role): what this page controls - the key that vouches for code every connected game server will run, and that code
/// itself - is platform-wide and belongs to the platform operator, never to any tenant. Refusals come back as a plain
/// <c>400</c> whose body is a sentence written for the admin, which the Panel shows as is.
/// </remarks>
[ApiController]
[Route("api/admin/plugin")]
[Authorize(Policy = "ManagePlatformSettings")]
public class PluginAdminController(
    IPluginKeyService keys,
    IPluginReleaseService releases,
    ApiDbContext context,
    IPluginScriptService scripts,
    IPluginAdminAudit audit,
    IPluginRollout rollout,
    TimeProvider clock) : ControllerBase
{
    /// <summary>The shortest reason accepted for signing a one-off file. The audit line is only worth something if it says why.</summary>
    public const int MinSigningNoteLength = 3;

    private string Actor =>
        User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? User.Identity?.Name ?? "unknown";

    // ---- keys ----------------------------------------------------------------------------------------------

    [HttpGet("keys")]
    public async Task<ActionResult<List<PluginKeyDto>>> ListKeys() =>
        Ok((await keys.ListAsync()).Select(ToDto).ToList());

    /// <summary>
    /// Whether the active key has gone long enough without being rotated that a reminder should be shown. Read by the banner every site
    /// administrator sees; never changes anything.
    /// </summary>
    [HttpGet("keys/reminder")]
    public async Task<ActionResult<PluginKeyReminderDto>> KeyReminder()
    {
        var reminder = await keys.GetReminderAsync();
        return Ok(new PluginKeyReminderDto
        {
            Due = reminder.Due,
            Fingerprint = reminder.Fingerprint,
            ActiveSinceUtc = reminder.ActiveSinceUtc,
            AgeDays = reminder.AgeDays,
            ReminderDays = reminder.ReminderDays
        });
    }

    [HttpPost("keys/rotate")]
    public async Task<ActionResult<PluginKeyDto>> Rotate([FromBody] RotatePluginKeyRequestDto request)
    {
        try
        {
            return Ok(ToDto(await keys.RotateAsync(Actor, request.Note, request.RevokeCurrent, request.RevokeReason)));
        }
        catch (PluginKeyOperationException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
    }

    [HttpPost("keys/{fingerprint}/revoke")]
    public async Task<ActionResult<PluginKeyDto>> Revoke(string fingerprint, [FromBody] RevokePluginKeyRequestDto request)
    {
        try
        {
            return Ok(ToDto(await keys.RevokeAsync(fingerprint, request.Reason, Actor)));
        }
        catch (PluginKeyOperationException ex) when (ex.Code == "not_found")
        {
            return NotFound();
        }
        catch (PluginKeyOperationException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Exports every signing key into a passphrase-protected file (see <see cref="PluginKeyBundle"/>), for a backup or to carry to
    /// another Panel. The response is the file; it is never cached. The passphrase and the file are never logged.
    /// </summary>
    [HttpPost("keys/export")]
    public async Task<IActionResult> ExportKeys([FromBody] ExportPluginKeysRequestDto request)
    {
        try
        {
            var export = await keys.ExportAsync(request.Passphrase, Actor);
            Response.Headers.CacheControl = "no-store";
            return File(Encoding.UTF8.GetBytes(export.Json), "application/json", export.FileName);
        }
        catch (PluginKeyOperationException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Imports an exported bundle, or with <c>DryRun</c> only reports what importing would do. Keys are added to the history; the
    /// active key changes only if <c>ActivateBundleKey</c> is set.
    /// </summary>
    [HttpPost("keys/import")]
    [RequestSizeLimit(PluginKeyBundle.MaxBundleBytes + 8192)]
    public async Task<ActionResult<PluginKeyImportResultDto>> ImportKeys([FromBody] ImportPluginKeysRequestDto request)
    {
        try
        {
            var result = await keys.ImportAsync(
                request.Bundle, request.Passphrase, request.ActivateBundleKey, request.DryRun, Actor, request.Note);
            return Ok(new PluginKeyImportResultDto
            {
                DryRun = result.DryRun,
                ActiveBefore = result.ActiveBefore,
                ActiveAfter = result.ActiveAfter,
                ChangesActiveKey = result.ChangesActiveKey,
                ChangesAnything = result.ChangesAnything,
                Items = result.Items.Select(i => new PluginKeyImportItemDto
                {
                    Fingerprint = i.Fingerprint,
                    BundleState = i.BundleState?.ToString().ToLowerInvariant() ?? string.Empty,
                    Action = i.Action
                }).ToList()
            });
        }
        catch (PluginKeyOperationException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
    }

    // ---- releases ------------------------------------------------------------------------------------------

    [HttpGet("releases")]
    public async Task<ActionResult<PluginReleasesDto>> ListReleases()
    {
        var served = new PluginReleasesDto
        {
            Main = await ServedAsync(PluginReleaseKind.Main),
            Updater = await ServedAsync(PluginReleaseKind.Updater),
            Releases = (await releases.ListAsync()).Select(ToDto).ToList()
        };
        return Ok(served);
    }

    /// <summary>
    /// Uploads a plugin source file as a draft. Multipart: <c>kind</c> (<c>main</c> or <c>updater</c>), <c>file</c>,
    /// optional <c>notes</c>. Nothing is served until it is published.
    /// </summary>
    [HttpPost("releases")]
    [RequestSizeLimit(PluginReleaseService.MaxBytes + 4096)]
    public async Task<ActionResult<PluginReleaseDto>> Upload([FromForm] string kind, [FromForm] string? notes, IFormFile file)
    {
        if (!TryParseKind(kind, out var releaseKind))
        {
            return BadRequest("Kind must be 'main' or 'updater'.");
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest("Choose a file to upload.");
        }

        if (file.Length > PluginReleaseService.MaxBytes)
        {
            return BadRequest($"The file is over {PluginReleaseService.MaxBytes / 1024} KiB.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);

        try
        {
            return Ok(ToDto(await releases.UploadAsync(releaseKind, buffer.ToArray(), Actor, notes)));
        }
        catch (PluginReleaseException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Signs a file that is not going to be published: multipart <c>kind</c>, <c>file</c> and a required <c>note</c> saying why. It is held to every
    /// check an upload is, signed with the active key exactly as a served file is, returned as the download, and recorded in the audit log with
    /// who asked, what (checksum, version) and why. Nothing is stored and nothing is delivered to any server: this is how a self-hoster gets a
    /// custom build they can install by hand on a server that trusts this Panel.
    /// </summary>
    [HttpPost("sign")]
    [RequestSizeLimit(PluginReleaseService.MaxBytes + 4096)]
    public async Task<IActionResult> SignFile([FromForm] string kind, [FromForm] string? note, IFormFile file)
    {
        if (!TryParseKind(kind, out var releaseKind))
        {
            return BadRequest("Kind must be 'main' or 'updater'.");
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest("Choose a file to sign.");
        }

        if (file.Length > PluginReleaseService.MaxBytes)
        {
            return BadRequest($"The file is over {PluginReleaseService.MaxBytes / 1024} KiB.");
        }

        var reason = note?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length < MinSigningNoteLength)
        {
            return BadRequest("Say why this file is being signed. The audit log records it.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);

        PluginValidatedSource validated;
        try
        {
            validated = releases.Validate(releaseKind, buffer.ToArray());
        }
        catch (PluginReleaseException ex)
        {
            return Refused(ex.Code, ex.Message);
        }

        var script = await scripts.SignSourceAsync(validated.Text, releaseKind);
        await audit.RecordAsync(
            PluginAdminEventKind.FileSigned, $"{releaseKind} {validated.Version}", Actor,
            $"sha256 {validated.Sha256[..12]}, key {script.KeyFingerprint}: {reason}");

        Response.Headers.CacheControl = "no-store";
        return File(script.Bytes, "text/plain", SignedFileName(releaseKind));
    }

    /// <summary>
    /// A stored draft's or published release signed with the active key, as the download: what to hand-install on a test server before publishing
    /// it (or on one that is not to be updated from here). Audit-logged with who asked. A withdrawn release is refused.
    /// </summary>
    [HttpGet("releases/{id:guid}/download")]
    public async Task<IActionResult> DownloadRelease(Guid id)
    {
        PluginStoredSource stored;
        try
        {
            stored = await releases.GetSourceAsync(id);
        }
        catch (PluginReleaseException ex) when (ex.Code == "not_found")
        {
            return NotFound();
        }
        catch (PluginReleaseException ex)
        {
            return Refused(ex.Code, ex.Message);
        }

        var script = await scripts.SignSourceAsync(stored.Text, stored.Kind);
        await audit.RecordAsync(
            PluginAdminEventKind.ReleaseSigned, $"{stored.Kind} {stored.Version}", Actor,
            $"{stored.State.ToString().ToLowerInvariant()} release downloaded, key {script.KeyFingerprint}");

        Response.Headers.CacheControl = "no-store";
        return File(script.Bytes, "text/plain", SignedFileName(stored.Kind));
    }

    [HttpPost("releases/{id:guid}/publish")]
    public async Task<ActionResult<PluginReleaseDto>> Publish(Guid id)
    {
        try
        {
            return Ok(ToDto(await releases.PublishAsync(id, Actor)));
        }
        catch (PluginReleaseException ex) when (ex.Code == "not_found")
        {
            return NotFound();
        }
        catch (PluginReleaseException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
    }

    [HttpPost("releases/{id:guid}/withdraw")]
    public async Task<ActionResult<PluginReleaseDto>> Withdraw(Guid id, [FromBody] WithdrawPluginReleaseRequestDto request)
    {
        try
        {
            return Ok(ToDto(await releases.WithdrawAsync(id, request.Reason, Actor)));
        }
        catch (PluginReleaseException ex) when (ex.Code == "not_found")
        {
            return NotFound();
        }
        catch (PluginReleaseException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
    }

    // ---- audit log -----------------------------------------------------------------------------------------

    /// <summary>The most recent plugin admin actions, newest first (capped at 200).</summary>
    [HttpGet("events")]
    public async Task<ActionResult<List<PluginAdminEventDto>>> Events([FromQuery] int limit = 50)
    {
        var take = Math.Clamp(limit, 1, 200);
        var rows = await context.PluginAdminEvents.AsNoTracking().OrderByDescending(e => e.AtUtc).Take(take).ToListAsync();
        return Ok(rows.Select(e => new PluginAdminEventDto
        {
            AtUtc = e.AtUtc, Kind = e.Kind.ToString(), Subject = e.Subject, Actor = e.Actor, Detail = e.Detail
        }).ToList());
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private BadRequestObjectResult Refused(string code, string message) => BadRequest(message);

    private async Task<PluginServedDto> ServedAsync(PluginReleaseKind kind)
    {
        var served = await releases.ResolveAsync(kind);
        var progress = served.Version is null ? null : await rollout.PeekAsync(kind, served.Version, clock.GetUtcNow());
        return new PluginServedDto
        {
            Kind = KindName(kind),
            ServedVersion = served.Version,
            EmbeddedVersion = releases.EmbeddedVersion(kind),
            FromRelease = served.FromRelease,
            RolloutStartedUtc = progress?.StartedAtUtc,
            RolloutHours = progress?.Hours ?? 0,
            RolloutPercent = progress is null ? null : (int)Math.Floor(progress.Fraction * 100)
        };
    }

    private static bool TryParseKind(string? text, out PluginReleaseKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "main": kind = PluginReleaseKind.Main; return true;
            case "updater": kind = PluginReleaseKind.Updater; return true;
            default: kind = default; return false;
        }
    }

    private static string KindName(PluginReleaseKind kind) => kind == PluginReleaseKind.Main ? "main" : "updater";

    private static string SignedFileName(PluginReleaseKind kind) => kind == PluginReleaseKind.Main ? "RustArchon.cs" : "RustArchonUpdater.cs";

    private static PluginKeyDto ToDto(PluginKeyInfo k) => new()
    {
        Fingerprint = k.Fingerprint,
        State = k.State.ToString().ToLowerInvariant(),
        RetiredAtUtc = k.RetiredAtUtc,
        RevokedAtUtc = k.RevokedAtUtc,
        RevokedReason = k.RevokedReason,
        ServersReporting = k.ServersReporting,
        LastReportedUtc = k.LastReportedUtc,
        ActiveSinceUtc = k.ActiveSinceUtc
    };

    private static PluginReleaseDto ToDto(PluginReleaseInfo r) => new()
    {
        Id = r.Id,
        Kind = KindName(r.Kind),
        Version = r.Version,
        State = r.State.ToString().ToLowerInvariant(),
        IsServed = r.IsServed,
        Sha256 = r.Sha256,
        UploadedAtUtc = r.UploadedAtUtc,
        UploadedBy = r.UploadedBy,
        PublishedAtUtc = r.PublishedAtUtc,
        PublishedBy = r.PublishedBy,
        WithdrawnAtUtc = r.WithdrawnAtUtc,
        WithdrawnBy = r.WithdrawnBy,
        WithdrawnReason = r.WithdrawnReason,
        Notes = r.Notes
    };
}
