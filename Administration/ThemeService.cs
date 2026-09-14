// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Infrastructure.ThemeUpdates;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>The result of <see cref="ThemeService.UploadAsync"/>.</summary>
public record ThemeUploadResult(bool Success, Theme? Theme, IReadOnlyList<string> Errors)
{
    public static ThemeUploadResult Failed(IReadOnlyList<string> errors) => new(false, null, errors);

    public static ThemeUploadResult Succeeded(Theme theme) => new(true, theme, []);
}

/// <summary>The result of <see cref="ThemeService.ReplaceAsync"/> - distinct from "not found", which is
/// its own <c>null</c> outcome, matching this Api's usual style (see <see cref="ThemeUploadResult"/>).
/// Also what <see cref="ThemeService.InstallUpdateAsync"/> returns, since installing an update is just
/// <see cref="ThemeService.ReplaceAsync"/> fed a downloaded package instead of an admin-supplied one.</summary>
public record ThemeReplaceResult(bool Success, Theme? Theme, IReadOnlyList<string> Errors)
{
    public static ThemeReplaceResult Failed(IReadOnlyList<string> errors) => new(false, null, errors);

    public static ThemeReplaceResult Succeeded(Theme theme) => new(true, theme, []);
}

/// <summary>Why <see cref="ThemeService.DeleteAsync"/> refused - distinct from "not found", which is
/// its own <c>null</c>/<c>bool</c> outcome, not an exception, matching this Api's usual style.</summary>
public class ThemeInUseException(string themeName)
    : InvalidOperationException($"'{themeName}' is the active theme and can't be deleted while active.");

/// <summary>
/// Orchestrates the theming feature's upload/activate/delete lifecycle - the only class that knows how
/// a <see cref="Theme"/> row, its objects in <see cref="IObjectStorage"/>, the active-theme flag, and
/// <see cref="IAppGenerationCache"/> all need to move together. <see cref="ThemePackageValidator"/> does
/// the actual package validation; this class is what happens once a package has already passed it.
/// </summary>
public class ThemeService(
    IThemeRepository repository, IObjectStorage objectStorage, IAppGenerationCache appGeneration,
    IActiveThemeCache activeThemeCache, IThemeUpdateCheckClient updateCheckClient, TimeProvider timeProvider)
{
    /// <summary>The object-storage key prefix every one of a theme's files lives under.</summary>
    private static string PrefixFor(Guid themeId) => $"themes/{themeId:D}/";

    /// <summary>
    /// Validates <paramref name="packageStream"/> (see <see cref="ThemePackageValidator"/>) and, only if
    /// it passes, uploads every entry to object storage and records a new catalog row - its
    /// <see cref="Theme.Name"/>/<see cref="Theme.Version"/>/etc. all copied straight from the package's
    /// own validated <see cref="ThemeManifest"/>, never from a caller-supplied value. A validation
    /// failure writes nothing anywhere - object storage and Postgres are only ever touched once the
    /// whole package is known-good.
    /// </summary>
    /// <remarks>
    /// Always creates a new row - see <see cref="ReplaceAsync"/> for updating an existing one's content
    /// in place instead. Uploading a package genuinely is adding a new thing to the catalog (a real .zip
    /// someone is choosing to install); editing a theme you already have is a different action with a
    /// different, non-multiplying outcome, which is what <see cref="ReplaceAsync"/> is for.
    /// </remarks>
    /// <param name="source">
    /// Defaults to <see cref="ThemeSource.Uploaded"/> - correct for every caller except
    /// <see cref="ThemesController.Build"/>, the only place that passes <see cref="ThemeSource.Built"/>.
    /// </param>
    public async Task<ThemeUploadResult> UploadAsync(
        Stream packageStream, CancellationToken cancellationToken = default, ThemeSource source = ThemeSource.Uploaded)
    {
        ArgumentNullException.ThrowIfNull(packageStream);

        ThemeValidationResult validation;
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            validation = ThemePackageValidator.Validate(archive);
        }

        if (!validation.Success)
        {
            return ThemeUploadResult.Failed(validation.Errors);
        }

        var manifest = validation.Manifest!;

        // Assigned ourselves rather than left to the database's default generator - same reasoning as
        // CommunicationPublisher.QueueInternalAsync's own id: this has to be known before anything is
        // written to object storage, since it's what the key prefix every entry is stored under is
        // built from.
        var id = Guid.CreateVersion7();
        var prefix = PrefixFor(id);

        foreach (var entry in validation.Entries)
        {
            await objectStorage.PutAsync(prefix + entry.Path, entry.Content, entry.ContentType, cancellationToken);
        }

        var theme = new Theme
        {
            Id = id,
            Name = manifest.Name,
            Version = manifest.Version,
            Description = manifest.Description,
            AuthorName = manifest.AuthorName,
            AuthorEmail = manifest.AuthorEmail,
            Website = manifest.Website,
            UpdateUrl = manifest.UpdateUrl,
            SizeBytes = validation.Entries.Sum(e => e.Content.LongLength),
            AssetPaths = validation.Entries.Select(e => e.Path).ToArray(),
            IsActive = false,
            Source = source
        };

        var saved = await repository.AddAsync(theme);
        return ThemeUploadResult.Succeeded(saved);
    }

    /// <summary>
    /// Makes <paramref name="id"/> the platform's active theme, clearing whichever one was active
    /// before (if any) first and in a separate save - see <see cref="Theme.IsActive"/>'s remarks and
    /// <see cref="ApiDbContext"/>'s partial unique index for why this never briefly has two rows both
    /// true. Bumps <see cref="IAppGenerationCache"/> unconditionally: activating a theme is by
    /// definition a page-chrome change, exactly the class of update <c>PlatformSettingsRegistry.KeysAffectingRenderedChrome</c>
    /// exists for, just for a value that isn't a <c>PlatformSetting</c> row at all. Also writes
    /// through to <see cref="IActiveThemeCache"/>, so RustArchon.Panel can find the new active theme's
    /// id without a round trip back to this Api.
    /// </summary>
    /// <returns>The newly-active theme, or <c>null</c> if <paramref name="id"/> doesn't exist.</returns>
    public async Task<Theme?> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var theme = await repository.GetByIdAsync(id, includes: null);
        if (theme is null)
        {
            return null;
        }

        if (await repository.GetActiveAsync(cancellationToken) is { } currentlyActive && currentlyActive.Id != id)
        {
            currentlyActive.IsActive = false;
            await repository.UpdateAsync(currentlyActive);
        }

        theme.IsActive = true;
        var saved = await repository.UpdateAsync(theme);

        // Order matters only in that both happen after the Postgres write above lands - Postgres is
        // always the thing GetActiveAsync itself trusts; these two are both just telling someone else
        // about a change that already happened.
        await appGeneration.BumpAsync();
        await activeThemeCache.SetActiveAsync(saved.Id);

        return saved;
    }

    /// <summary>
    /// Deletes a theme's objects from object storage and soft-deletes its catalog row (see
    /// <c>JumpStart.Data.Auditing.AuditableEntity</c>'s remarks on the inherited <c>DeleteAsync</c>'s
    /// soft-delete behavior). Refuses outright if it's the currently-active theme - deleting the objects a live
    /// admin's chrome is currently rendering out from under it would break the site, not just the admin
    /// page, for as long as any browser tab has it cached.
    /// </summary>
    /// <returns><c>false</c> if <paramref name="id"/> doesn't exist.</returns>
    /// <exception cref="ThemeInUseException"><paramref name="id"/> is the active theme.</exception>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var theme = await repository.GetByIdAsync(id, includes: null);
        if (theme is null)
        {
            return false;
        }

        if (theme.IsActive)
        {
            throw new ThemeInUseException(theme.Name);
        }

        await objectStorage.DeleteByPrefixAsync(PrefixFor(id), cancellationToken);
        return await repository.DeleteAsync(id);
    }

    /// <summary>
    /// Reads one of <paramref name="id"/>'s own assets back out of object storage - what the theme-builder's
    /// "edit an existing theme" flow uses to pull a theme's current <c>theme.css</c> and any images/fonts
    /// back into the Panel before letting the admin republish a modified version (see
    /// <c>ThemesController.GetAsset</c>). <paramref name="relativePath"/> is checked against
    /// <see cref="Theme.AssetPaths"/> first - never handed to <see cref="IObjectStorage"/> unchecked -
    /// so this can only ever read back a file the theme's own package actually contains, not an
    /// arbitrary object-storage key under its prefix.
    /// </summary>
    /// <returns><c>null</c> if <paramref name="id"/> doesn't exist, or if <paramref name="relativePath"/>
    /// isn't one of its own <see cref="Theme.AssetPaths"/>.</returns>
    public async Task<ObjectContent?> GetAssetAsync(
        Guid id, string relativePath, CancellationToken cancellationToken = default)
    {
        var theme = await repository.GetByIdAsync(id, includes: null);
        if (theme is null || !theme.AssetPaths.Contains(relativePath))
        {
            return null;
        }

        return await objectStorage.GetAsync(PrefixFor(id) + relativePath, cancellationToken);
    }

    /// <summary>
    /// Validates <paramref name="packageStream"/> exactly like <see cref="UploadAsync"/> does, but
    /// updates <paramref name="id"/>'s own existing row and object-storage content in place instead of
    /// creating a new one - what the Panel's "Edit → Save" and <see cref="InstallUpdateAsync"/> both use.
    /// <see cref="Theme.Id"/>, <see cref="Theme.IsActive"/>, and <see cref="Theme.CreatedOn"/> are
    /// untouched; every manifest-derived field and the object-storage content are replaced wholesale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Writes new content before removing anything old.</strong> Every entry in the new package
    /// is written first (each simply overwrites whatever was at that same key before); only once that has
    /// fully succeeded are the old assets no longer present in the new package removed, one at a time.
    /// A failure partway through the write phase leaves the theme serving a harmless mix of old and new
    /// files rather than - had this instead deleted everything up front - a theme missing content
    /// entirely. The removal phase deletes only files the new package doesn't have; it isn't itself
    /// expected to fail, but if one delete does, the theme is left with one harmless orphaned object
    /// rather than anything broken.
    /// </para>
    /// <para>
    /// A validation failure writes nothing anywhere, same as <see cref="UploadAsync"/> - the existing
    /// theme is left completely unchanged.
    /// </para>
    /// </remarks>
    /// <param name="source">When given, overwrites <see cref="Theme.Source"/> too - what the Panel's
    /// "Edit → Save" passes (<see cref="ThemeSource.Built"/>: once you've saved your own edit over it,
    /// the row's content genuinely is Panel-built, regardless of where it started). <c>null</c> (the
    /// default) leaves <see cref="Theme.Source"/> exactly as it already was - what
    /// <see cref="InstallUpdateAsync"/> wants, since installing a real downloaded package doesn't change
    /// whether the theme originated as an upload.</param>
    /// <returns><c>null</c> if <paramref name="id"/> doesn't exist.</returns>
    public async Task<ThemeReplaceResult?> ReplaceAsync(
        Guid id, Stream packageStream, CancellationToken cancellationToken = default, ThemeSource? source = null)
    {
        var theme = await repository.GetByIdAsync(id, includes: null);
        if (theme is null)
        {
            return null;
        }

        ThemeValidationResult validation;
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            validation = ThemePackageValidator.Validate(archive);
        }

        if (!validation.Success)
        {
            return ThemeReplaceResult.Failed(validation.Errors);
        }

        var manifest = validation.Manifest!;
        var prefix = PrefixFor(id);
        var newPaths = validation.Entries.Select(e => e.Path).ToHashSet();

        foreach (var entry in validation.Entries)
        {
            await objectStorage.PutAsync(prefix + entry.Path, entry.Content, entry.ContentType, cancellationToken);
        }

        foreach (var stalePath in theme.AssetPaths.Where(p => !newPaths.Contains(p)))
        {
            await objectStorage.DeleteAsync(prefix + stalePath, cancellationToken);
        }

        theme.Name = manifest.Name;
        theme.Version = manifest.Version;
        theme.Description = manifest.Description;
        theme.AuthorName = manifest.AuthorName;
        theme.AuthorEmail = manifest.AuthorEmail;
        theme.Website = manifest.Website;
        theme.UpdateUrl = manifest.UpdateUrl;
        theme.SizeBytes = validation.Entries.Sum(e => e.Content.LongLength);
        theme.AssetPaths = validation.Entries.Select(e => e.Path).ToArray();

        if (source is { } explicitSource)
        {
            theme.Source = explicitSource;
        }

        var saved = await repository.UpdateAsync(theme);

        // Only matters if this is the theme currently live - same "activating/changing a theme is a
        // page-chrome change" reasoning as ActivateAsync, just conditional here since replacing an
        // inactive theme's content changes nothing anyone is currently looking at.
        if (saved.IsActive)
        {
            await appGeneration.BumpAsync();
        }

        return ThemeReplaceResult.Succeeded(saved);
    }

    /// <summary>
    /// Checks <paramref name="id"/>'s own <see cref="Theme.UpdateUrl"/> (if it has one) for a newer
    /// <see cref="Theme.Version"/> and records the outcome on the row - <see cref="Theme.LatestKnownVersion"/>/
    /// <see cref="Theme.LastUpdateCheckError"/> on success or failure respectively, and
    /// <see cref="Theme.LastUpdateCheckOn"/> either way. Admin-triggered only (the Panel's own "Check for
    /// updates" button) - deliberately not run by any background job, so this fetch only ever happens
    /// when someone has actually asked for it. A theme's <see cref="Theme.UpdateUrl"/> is untrusted,
    /// possibly third-party-authored content, and even with <see cref="ThemeUpdateCheckClient"/>'s own
    /// SSRF hardening, a standing scheduled call to it is a strictly bigger surface than one that only
    /// ever fires on demand.
    /// </summary>
    /// <returns>The theme as it now stands (unchanged if it has no <see cref="Theme.UpdateUrl"/>), or
    /// <c>null</c> if <paramref name="id"/> doesn't exist.</returns>
    public async Task<Theme?> CheckForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var theme = await repository.GetByIdAsync(id, includes: null);
        if (theme is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(theme.UpdateUrl))
        {
            return theme;
        }

        var outcome = await updateCheckClient.CheckAsync(theme.UpdateUrl, cancellationToken);
        ApplyCheckOutcome(theme, outcome);

        return await repository.UpdateAsync(theme);
    }

    private void ApplyCheckOutcome(Theme theme, ThemeUpdateCheckOutcome outcome)
    {
        theme.LastUpdateCheckOn = timeProvider.GetUtcNow();

        if (outcome.Success)
        {
            theme.LatestKnownVersion = outcome.RemoteVersion;
            theme.LatestDownloadPackageUrl = outcome.PackageUrl;
            theme.LastUpdateCheckError = null;
        }
        else
        {
            // Deliberately leaves LatestKnownVersion (and LatestDownloadPackageUrl) exactly as a
            // previous successful check left them - a transient failure (the author's own server is
            // briefly down) shouldn't make an already-known update disappear from the admin's view; only
            // a genuinely newer successful check ever moves them forward.
            theme.LastUpdateCheckError = outcome.Error;
        }
    }

    /// <summary>
    /// Downloads, validates, and installs the update <paramref name="id"/>'s own <c>UpdateUrl</c> most
    /// recently reported - what the Panel's "Update to..." confirmation drives. Always re-runs the
    /// version check first (see <see cref="CheckForUpdateAsync"/>) rather than trusting whatever
    /// <see cref="Theme.LatestKnownVersion"/>/<see cref="Theme.LatestDownloadPackageUrl"/> already held:
    /// this is a real "go fetch and install a package" action, not just a status refresh, so it deserves
    /// its own fresh answer for "is this genuinely still the latest, and where do I get it" rather than
    /// installing from a possibly stale cached URL.
    /// </summary>
    /// <remarks>
    /// Installs by calling <see cref="ReplaceAsync"/> - an update replaces this exact theme's content in
    /// place, the same as an admin re-saving it after an edit, rather than piling up a new catalog row
    /// every time a real update comes in. <see cref="Theme.Source"/> is left alone (installing a real
    /// downloaded package doesn't change whether this theme originated as an upload).
    /// </remarks>
    /// <returns><c>null</c> if <paramref name="id"/> doesn't exist; otherwise the outcome, successful or
    /// not (see <see cref="ThemeReplaceResult"/>).</returns>
    public async Task<ThemeReplaceResult?> InstallUpdateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var current = await repository.GetByIdAsync(id, includes: null);
        if (current is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(current.UpdateUrl))
        {
            return ThemeReplaceResult.Failed(["This theme has no update URL configured."]);
        }

        var checkOutcome = await updateCheckClient.CheckAsync(current.UpdateUrl, cancellationToken);
        ApplyCheckOutcome(current, checkOutcome);
        await repository.UpdateAsync(current);

        if (!checkOutcome.Success)
        {
            return ThemeReplaceResult.Failed([checkOutcome.Error!]);
        }

        if (!ThemeVersionComparer.IsNewer(current.Version, checkOutcome.RemoteVersion!))
        {
            return ThemeReplaceResult.Failed(["This theme is already on the latest version."]);
        }

        if (string.IsNullOrWhiteSpace(checkOutcome.PackageUrl))
        {
            return ThemeReplaceResult.Failed(["The update feed didn't include a package to install."]);
        }

        var download = await updateCheckClient.DownloadPackageAsync(checkOutcome.PackageUrl, cancellationToken);
        if (!download.Success)
        {
            return ThemeReplaceResult.Failed([download.Error!]);
        }

        // The downloaded package is never trusted just because it came from a URL the update feed named -
        // it goes through ReplaceAsync exactly like a manual admin edit would, manifest validation and
        // all.
        using var packageStream = new MemoryStream(download.Content!);
        return await ReplaceAsync(id, packageStream, cancellationToken);
    }
}
