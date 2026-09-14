// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Write-through signal for which <see cref="Data.Theme"/> is currently active, read directly by
/// RustArchon.Panel via the same shared Valkey cache <see cref="PlatformSettingsCache"/>/
/// <see cref="IAppGenerationCache"/> already use - not a second, Api-local cache of its own.
/// </summary>
/// <remarks>
/// Deliberately write-only from this Api's own point of view: <c>ThemeService</c> always reads the
/// active theme back from Postgres (<c>IThemeRepository.GetActiveAsync</c>), never from here - Postgres
/// stays the single durable source of truth, this cache exists purely so Panel can find the active
/// theme's id on every page render without a network round trip back to this Api just to ask (the same
/// reasoning <see cref="Data.PlatformSetting"/> values are cached at all). No Postgres-backed fallback
/// the way <c>SiteBrandingService</c>'s Panel-side counterpart has either: a page rendering without its
/// active theme's extra stylesheet for as long as Valkey is cold is a fully acceptable degrade (the
/// baked-in default look still applies - see <c>MainLayout.razor</c>'s remarks), unlike a nav bar with
/// no name at all.
/// </remarks>
public interface IActiveThemeCache
{
    /// <summary>Records <paramref name="themeId"/> as the platform's active theme - called once,
    /// from <c>ThemeService.ActivateAsync</c>, right after the same change lands in Postgres.</summary>
    Task SetActiveAsync(Guid themeId);
}
