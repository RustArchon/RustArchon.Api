// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin management of uploaded theme packages: list, upload, activate, delete. See
/// <see cref="ThemeService"/> for the actual upload/activate/delete orchestration and
/// <see cref="Administration.ThemePackageValidator"/> for what a package is required to look like.
/// </summary>
/// <remarks>
/// Gated by <see cref="PermissionCatalog.PlatformManageSettings"/> - the same permission
/// <c>PlatformSettingsController</c> requires, since a platform-wide theme is branding/chrome
/// configuration in the same sense <c>SiteName</c>/<c>SiteUrl</c> are, not a per-tenant concern.
/// </remarks>
[ApiController]
[Route("api/themes")]
[RequirePermission(PermissionCatalog.PlatformManageSettings)]
public class ThemesController(IThemeRepository repository, ThemeService themeService) : ControllerBase
{
    /// <summary>Whether <paramref name="t"/>'s own <c>UpdateUrl</c> has reported something newer than
    /// its <see cref="Theme.Version"/> - see <see cref="ThemeSummaryDto.UpdateAvailable"/>'s remarks on
    /// why this is computed here rather than stored.</summary>
    private static bool ComputeUpdateAvailable(Theme t) =>
        t.LatestKnownVersion is { } latest && ThemeVersionComparer.IsNewer(t.Version, latest);

    private static ThemeSummaryDto ToSummaryDto(Theme t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Version = t.Version,
        SizeBytes = t.SizeBytes,
        IsActive = t.IsActive,
        UploadedOn = t.CreatedOn,
        Source = t.Source,
        UpdateAvailable = ComputeUpdateAvailable(t)
    };

    private static ThemeDetailDto ToDetailDto(Theme t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Version = t.Version,
        SizeBytes = t.SizeBytes,
        IsActive = t.IsActive,
        UploadedOn = t.CreatedOn,
        Source = t.Source,
        UpdateAvailable = ComputeUpdateAvailable(t),
        AssetPaths = t.AssetPaths.ToList(),
        Description = t.Description,
        AuthorName = t.AuthorName,
        AuthorEmail = t.AuthorEmail,
        Website = t.Website,
        UpdateUrl = t.UpdateUrl,
        LatestKnownVersion = t.LatestKnownVersion,
        LatestDownloadPackageUrl = t.LatestDownloadPackageUrl,
        LastUpdateCheckOn = t.LastUpdateCheckOn,
        LastUpdateCheckError = t.LastUpdateCheckError
    };

    /// <summary>Every uploaded theme, newest first. Unpaginated - same reasoning as
    /// <c>PlatformSettingsController.GetAll</c>, an admin-only list expected to stay small.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ThemeSummaryDto>>> List(CancellationToken cancellationToken)
    {
        var themes = await repository.GetAllOrderedAsync(cancellationToken);
        return Ok(themes.Select(ToSummaryDto).ToList());
    }

    /// <summary>One theme in full, including every asset path it contains.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ThemeDetailDto>> GetById(Guid id)
    {
        var theme = await repository.GetByIdAsync(id, includes: null);
        return theme is null ? NotFound() : Ok(ToDetailDto(theme));
    }

    /// <summary>
    /// Uploads a theme package (a zip file) as multipart form data - <c>package</c> the zip itself, and
    /// nothing else: its name/version/author/etc. all come from its own <c>manifest.json</c>, never a
    /// caller-supplied value (see <see cref="ThemeService.UploadAsync"/>'s remarks). Rejects the whole
    /// package (writing nothing anywhere) if <see cref="Administration.ThemePackageValidator"/> finds
    /// anything wrong with it - see <see cref="ThemeUploadErrorDto"/>.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(25 * 1024 * 1024)]
    // See the remarks above [RequirePermission] on the class for why this exists: [ApiController]
    // infers an implicit "multipart/form-data only" constraint for any action binding an IFormFile/
    // [FromForm] parameter, and that constraint is enforced during action *selection* (routing) -
    // which runs before UseAuthorization ever gets a matched endpoint to check. A caller with no
    // permission sending any other content-type (including PermissionMatrixTests' own application/json
    // probe) never reached this action at all; routing rejected the request with a raw 415 first,
    // before authorization had a chance to refuse it with 403. Broadening this to accept the wrong
    // content-type too lets routing always select this action, so [RequirePermission] runs first as
    // intended - the existing null/empty check below still cleanly rejects a request that genuinely
    // isn't multipart/form-data (IFormFile binding just comes back null, not an exception).
    [Consumes("multipart/form-data", "application/json")]
    public async Task<ActionResult<ThemeDetailDto>> Upload(
        // No [FromForm] here - deliberately. ASP.NET Core already binds a bare IFormFile parameter from
        // form data on its own; adding the attribute explicitly is what breaks Swashbuckle's swagger.json
        // generation (a known Swashbuckle bug - "[FromForm] attribute used with IFormFile" - the
        // exception it throws links straight to this exact fix). No change in actual request binding,
        // only in whether /swagger/v1/swagger.json 500s.
        IFormFile package, CancellationToken cancellationToken)
    {
        if (package is null || package.Length == 0)
        {
            return BadRequest("A package file is required.");
        }

        await using var stream = package.OpenReadStream();
        var result = await themeService.UploadAsync(stream, cancellationToken);

        if (!result.Success)
        {
            return UnprocessableEntity(new ThemeUploadErrorDto { Errors = result.Errors.ToList() });
        }

        return Ok(ToDetailDto(result.Theme!));
    }

    /// <summary>
    /// Builds a theme package from the Panel's own theme-builder form - manifest fields, CSS content,
    /// and any images/fonts - instead of a pre-made .zip upload. <see cref="Administration.ThemePackageBuilder"/>
    /// only assembles the pieces into the same package shape <see cref="Upload"/> itself accepts; the
    /// assembled result then goes through the exact same <see cref="ThemeService.UploadAsync"/> call
    /// (manifest validation, size limits, extension allowlist, folder placement rules, all of it) - a
    /// theme built here is validated identically to one uploaded as a file, not by a second copy of
    /// those rules. Uses the same <see cref="ThemeUploadErrorDto"/> shape as <see cref="Upload"/> for
    /// either kind of failure (assembly or validation), so the Panel's existing error-handling for a
    /// failed upload works unchanged here too.
    /// </summary>
    [HttpPost("build")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    // See Upload's own remarks on why this is here - same [FromForm]-implies-multipart-only
    // action-selection constraint, same fix.
    [Consumes("multipart/form-data", "application/json")]
    public async Task<ActionResult<ThemeDetailDto>> Build(
        [FromForm] ThemeBuildRequest request, CancellationToken cancellationToken)
    {
        var built = ThemePackageBuilder.Build(request);
        if (!built.Success)
        {
            return UnprocessableEntity(new ThemeUploadErrorDto { Errors = built.Errors.ToList() });
        }

        await using var package = built.Package!;
        var result = await themeService.UploadAsync(package, cancellationToken, ThemeSource.Built);

        if (!result.Success)
        {
            return UnprocessableEntity(new ThemeUploadErrorDto { Errors = result.Errors.ToList() });
        }

        return Ok(ToDetailDto(result.Theme!));
    }

    /// <summary>
    /// Rebuilds an existing theme's content in place from the Panel's own theme-builder form - the
    /// "Edit → Save" action. Same assembly/validation as <see cref="Build"/>, but the result replaces
    /// <paramref name="id"/>'s own content (see <see cref="ThemeService.ReplaceAsync"/>) instead of
    /// creating a new theme - saving an edit updates the theme you started from, it doesn't leave a new
    /// row behind every time. Use <see cref="Build"/> instead (the Panel's "Save as New Theme") when a
    /// separate copy is actually what's wanted.
    /// </summary>
    [HttpPost("{id:guid}/build")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    // See Upload's own remarks on why this is here - same [FromForm]-implies-multipart-only
    // action-selection constraint, same fix.
    [Consumes("multipart/form-data", "application/json")]
    public async Task<ActionResult<ThemeDetailDto>> Rebuild(
        Guid id, [FromForm] ThemeBuildRequest request, CancellationToken cancellationToken)
    {
        var built = ThemePackageBuilder.Build(request);
        if (!built.Success)
        {
            return UnprocessableEntity(new ThemeUploadErrorDto { Errors = built.Errors.ToList() });
        }

        await using var package = built.Package!;
        var result = await themeService.ReplaceAsync(id, package, cancellationToken, ThemeSource.Built);

        if (result is null)
        {
            return NotFound();
        }

        return result.Success
            ? Ok(ToDetailDto(result.Theme!))
            : UnprocessableEntity(new ThemeUploadErrorDto { Errors = result.Errors.ToList() });
    }

    /// <summary>
    /// Reads one of this theme's own assets back - <c>theme.css</c>, or one of its images/fonts - for
    /// the Panel's "edit this theme" flow (see <see cref="ThemeService.GetAssetAsync"/>), which needs a
    /// theme's actual current content before it can pre-fill the builder form with it. Not a general
    /// asset-serving endpoint: gated by the same admin permission as the rest of this controller, unlike
    /// the shared-secret-gated <c>InternalController.ThemeAsset</c> a live page's stylesheet link
    /// actually renders through.
    /// </summary>
    [HttpGet("{id:guid}/assets/{**path}")]
    public async Task<ActionResult> GetAsset(Guid id, string path, CancellationToken cancellationToken)
    {
        var asset = await themeService.GetAssetAsync(id, path, cancellationToken);
        return asset is null ? NotFound() : File(asset.Content, asset.ContentType);
    }

    /// <summary>Makes this the platform's active theme, replacing whichever one was active before.</summary>
    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<ThemeDetailDto>> Activate(Guid id, CancellationToken cancellationToken)
    {
        var activated = await themeService.ActivateAsync(id, cancellationToken);
        return activated is null ? NotFound() : Ok(ToDetailDto(activated));
    }

    /// <summary>Checks this theme's own <c>UpdateUrl</c> (if it has one) right now - see
    /// <see cref="ThemeService.CheckForUpdateAsync"/>'s remarks on why this is admin-triggered only.</summary>
    [HttpPost("{id:guid}/check-update")]
    public async Task<ActionResult<ThemeDetailDto>> CheckForUpdate(Guid id, CancellationToken cancellationToken)
    {
        var theme = await themeService.CheckForUpdateAsync(id, cancellationToken);
        return theme is null ? NotFound() : Ok(ToDetailDto(theme));
    }

    /// <summary>
    /// Downloads and installs the update this theme's <c>UpdateUrl</c> reports - see
    /// <see cref="ThemeService.InstallUpdateAsync"/>. Rejects with the same <see cref="ThemeUploadErrorDto"/>
    /// shape as <see cref="Upload"/> when the download or the package itself fails validation.
    /// </summary>
    [HttpPost("{id:guid}/install-update")]
    public async Task<ActionResult<ThemeDetailDto>> InstallUpdate(Guid id, CancellationToken cancellationToken)
    {
        var result = await themeService.InstallUpdateAsync(id, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        return result.Success
            ? Ok(ToDetailDto(result.Theme!))
            : UnprocessableEntity(new ThemeUploadErrorDto { Errors = result.Errors.ToList() });
    }

    /// <summary>
    /// Deletes a theme - refused with 409 while it's the active one (see
    /// <see cref="ThemeService.DeleteAsync"/>'s remarks on why).
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await themeService.DeleteAsync(id, cancellationToken);
            return deleted ? NoContent() : NotFound();
        }
        catch (ThemeInUseException ex)
        {
            return Conflict(ex.Message);
        }
    }
}
