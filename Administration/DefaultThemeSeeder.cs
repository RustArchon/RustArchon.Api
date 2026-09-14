// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Administration;

/// <summary>
/// Seeds the platform's own built-in look as a first-class <see cref="Data.Theme"/> row, through the
/// exact same <see cref="ThemeService.UploadAsync"/>/<see cref="ThemePackageValidator"/> path a real
/// admin upload goes through - dogfooding the theming pipeline on day one, and catching a malformed
/// seed package the same way a malformed admin upload would be caught, rather than writing around the
/// validator for "trusted" content.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The embedded <c>theme.css</c> is a copy, not the source of truth.</strong>
/// <c>RustArchon.Panel</c>'s own <c>wwwroot/app.css</c> is what the app actually links and renders
/// from - unconditionally, regardless of whether any theme is active or Garage is even reachable (see
/// <c>MainLayout.razor</c>'s remarks) - so the app is never unstyled by a theming-specific outage. This
/// embedded copy exists only so <see cref="ThemeName"/> is a real, selectable catalog entry an admin
/// can see and revert to through the same UI a custom upload uses, and so activating a custom theme
/// and reverting to this one are symmetric operations. Keeping the two copies in sync is a manual
/// discipline, not enforced by anything - a visual change to <c>app.css</c> should be mirrored into
/// <c>Administration/DefaultTheme/theme.css</c>, but nothing currently reminds anyone to. Worth
/// automating (a build step, a shared source file) if the drift this risks ever actually causes
/// confusion; not built speculatively here.
/// </para>
/// <para>
/// Gracefully skipped, not fatal, if Garage isn't reachable/configured yet - same posture as Valkey
/// elsewhere in this Api: a deployment that hasn't run the Garage bootstrap yet (see
/// <c>DEPLOYMENT.md</c>) still starts up fine, just without this seeded theme (and thus without a row
/// in the Themes admin list at all) until a restart after Garage is configured.
/// </para>
/// </remarks>
public static class DefaultThemeSeeder
{
    /// <summary>The seeded theme's <see cref="Data.Theme.Name"/> - also what identifies it as already
    /// seeded, the same idempotency check <see cref="PlatformSettingsRegistry"/> uses by
    /// <see cref="Data.PlatformSetting.Key"/>.</summary>
    public const string ThemeName = "RustArchon";

    public static async Task EnsureSeededAsync(
        IThemeRepository repository, ThemeService themeService, ILogger logger)
    {
        var existing = await repository.GetAllOrderedAsync();
        if (existing.Any(t => t.Name == ThemeName))
        {
            return;
        }

        try
        {
            await using var package = BuildSeedPackageZip();
            var result = await themeService.UploadAsync(package);

            if (!result.Success)
            {
                // A bug in the embedded seed content itself, not an environment problem - loud, since
                // no deployment-side configuration fix could possibly address this.
                logger.LogError(
                    "The built-in '{ThemeName}' theme's embedded package failed validation: {Errors}",
                    ThemeName, string.Join("; ", result.Errors));
                return;
            }

            // A fresh deployment starts with this as the active theme - the same thing it always
            // implicitly was before theming existed at all. Activating it here just makes that
            // explicit, through the same mechanism any other theme's activation uses.
            await themeService.ActivateAsync(result.Theme!.Id);

            logger.LogInformation("Seeded and activated the built-in '{ThemeName}' theme.", ThemeName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Couldn't seed the built-in '{ThemeName}' theme - object storage may not be configured " +
                "yet (see DEPLOYMENT.md's Garage bootstrap step). RustArchon.Panel's own baked-in " +
                "stylesheet still applies regardless; this only affects whether '{ThemeName}' shows up " +
                "in the Themes admin page.",
                ThemeName, ThemeName);
        }
    }

    /// <summary>Builds an in-memory zip from the two embedded resources - the same shape
    /// <see cref="ThemePackageValidator"/> expects from a real upload.</summary>
    private static MemoryStream BuildSeedPackageZip()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEmbeddedEntry(archive, assembly, "RustArchon.Api.Administration.DefaultTheme.manifest.json", "manifest.json");
            AddEmbeddedEntry(archive, assembly, "RustArchon.Api.Administration.DefaultTheme.theme.css", "theme.css");
        }

        stream.Position = 0;
        return stream;
    }

    private static void AddEmbeddedEntry(ZipArchive archive, Assembly assembly, string resourceName, string entryName)
    {
        using var resourceStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found - check RustArchon.Api.csproj's LogicalName.");

        using var entryStream = archive.CreateEntry(entryName).Open();
        resourceStream.CopyTo(entryStream);
    }
}
