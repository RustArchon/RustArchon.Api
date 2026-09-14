// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.ThemeUpdates;

/// <summary>The result of one <see cref="IThemeUpdateCheckClient.CheckAsync"/> call. Never carries a raw
/// exception message or any other part of the remote response on failure - see
/// <see cref="ThemeUpdateCheckClient"/>'s remarks on why that boundary is deliberate.</summary>
public record ThemeUpdateCheckOutcome(bool Success, string? RemoteVersion, string? PackageUrl, string? Error)
{
    public static ThemeUpdateCheckOutcome Succeeded(string remoteVersion, string? packageUrl) =>
        new(true, remoteVersion, packageUrl, null);

    public static ThemeUpdateCheckOutcome Failed(string error) => new(false, null, null, error);
}

/// <summary>The result of one <see cref="IThemeUpdateCheckClient.DownloadPackageAsync"/> call. Same
/// "never a raw exception or remote response text" rule as <see cref="ThemeUpdateCheckOutcome"/>.</summary>
public record ThemePackageDownloadOutcome(bool Success, byte[]? Content, string? Error)
{
    public static ThemePackageDownloadOutcome Succeeded(byte[] content) => new(true, content, null);

    public static ThemePackageDownloadOutcome Failed(string error) => new(false, null, error);
}

/// <summary>
/// Fetches a theme's own <c>UpdateUrl</c> to ask "is there a newer version?", and downloads the package
/// it names when an admin confirms installing it - see <see cref="ThemeUpdateCheckClient"/> for the only
/// real implementation and the SSRF-hardening that makes both calls safe against an untrusted,
/// package-supplied URL.
/// </summary>
public interface IThemeUpdateCheckClient
{
    Task<ThemeUpdateCheckOutcome> CheckAsync(string updateUrl, CancellationToken cancellationToken = default);

    Task<ThemePackageDownloadOutcome> DownloadPackageAsync(string packageUrl, CancellationToken cancellationToken = default);
}
