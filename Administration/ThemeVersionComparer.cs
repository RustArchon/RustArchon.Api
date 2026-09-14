// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Administration;

/// <summary>
/// Decides whether one theme version string should be reported as newer than another - the comparison
/// <see cref="ThemeService.CheckForUpdateAsync"/> runs between a theme's own <c>Version</c> and whatever
/// its <c>UpdateUrl</c> most recently reported.
/// </summary>
/// <remarks>
/// Deliberately not full SemVer precedence - see <c>Theme.Version</c>'s own remarks on why "just enough
/// to compare, not a general-purpose version library" is the right amount of machinery here. Only the
/// numeric major/minor/patch components are compared, plus whether each side carries a prerelease
/// suffix at all; prerelease identifiers themselves (e.g. whether <c>-beta.2</c> outranks <c>-beta.1</c>)
/// and build metadata are never compared. That is enough to answer "is there something new to tell the
/// admin about?" without pulling in a SemVer package for a feature that only ever displays the result,
/// never acts on it automatically.
/// </remarks>
public static class ThemeVersionComparer
{
    /// <summary>Whether <paramref name="value"/> is shaped like a version this comparer can parse - the
    /// same shape <see cref="ThemePackageValidator"/> already requires of every theme's own
    /// <c>Version</c>, reused here rather than duplicated so the two can never drift apart.</summary>
    public static bool LooksLikeVersion(string value) => ThemePackageValidator.VersionPattern.IsMatch(value.Trim());

    /// <summary>
    /// Whether <paramref name="remote"/> should be reported as an available update over
    /// <paramref name="local"/>. Never throws: a value on either side that doesn't
    /// <see cref="LooksLikeVersion"/> makes this return <c>false</c> ("nothing to report") rather than
    /// fail the caller - a malformed remote response is exactly the kind of untrusted input this method
    /// has to tolerate quietly (see <c>ThemeUpdates.ThemeUpdateCheckClient</c>'s own remarks on treating
    /// the remote manifest as untrusted).
    /// </summary>
    public static bool IsNewer(string local, string remote)
    {
        if (!TryParse(local, out var localParsed) || !TryParse(remote, out var remoteParsed))
        {
            return false;
        }

        var comparison = remoteParsed.Numeric.CompareTo(localParsed.Numeric);
        if (comparison != 0)
        {
            return comparison > 0;
        }

        // Same major.minor.patch: a final release outranks a prerelease of the same number (2.0.0 is
        // newer than 2.0.0-beta.1), but two prereleases of the same number - or two final releases -
        // are treated as equal rather than guessed at further.
        return localParsed.IsPrerelease && !remoteParsed.IsPrerelease;
    }

    private readonly record struct ParsedVersion((int Major, int Minor, int Patch) Numeric, bool IsPrerelease);

    private static bool TryParse(string value, out ParsedVersion parsed)
    {
        parsed = default;
        var trimmed = value.Trim();

        if (!LooksLikeVersion(trimmed))
        {
            return false;
        }

        // Build metadata never affects ordering; strip it before looking for a -prerelease suffix so a
        // value like "1.0.0-beta+001" isn't mistaken for having "+001" as part of the prerelease tag.
        var withoutBuild = trimmed.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        var isPrerelease = parts.Length > 1;

        var segments = parts[0].Split('.');
        int Segment(int index) => index < segments.Length ? int.Parse(segments[index]) : 0;

        parsed = new ParsedVersion((Segment(0), Segment(1), Segment(2)), isPrerelease);
        return true;
    }
}
