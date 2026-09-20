// Copyright ©2026 Scott Blomfield

using System.Linq;
using System.Text.RegularExpressions;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Version comparison for the RustArchon plugin: exactly three numeric parts, compared part by part, so
/// <c>0.10.0</c> is newer than <c>0.9.0</c> (a string comparison gets that wrong). The Updater applies the same rule
/// on the game server.
/// </summary>
/// <remarks>
/// Fails closed: anything that is not <c>major.minor.patch</c> is "not newer", so an odd or missing version can never
/// cause an update to be offered or started.
/// </remarks>
public static partial class PluginVersions
{
    /// <summary>Whether <paramref name="offered"/> is strictly newer than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? offered, string? installed)
    {
        if (!TryParse(offered, out var a) || !TryParse(installed, out var b))
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] > b[i];
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="version"/> is the same as or newer than <paramref name="minimum"/>. Unparseable is "no".</summary>
    public static bool IsAtLeast(string? version, string? minimum) =>
        IsValid(version) && IsValid(minimum) && (version == minimum || IsNewer(version, minimum) || Equivalent(version, minimum));

    // "0.2.0" and "0.02.0" are the same version; string equality alone would say otherwise.
    private static bool Equivalent(string? a, string? b) =>
        TryParse(a, out var x) && TryParse(b, out var y) && x.SequenceEqual(y);

    public static bool IsValid(string? version) => TryParse(version, out _);

    private static bool TryParse(string? version, out int[] parts)
    {
        parts = [];
        if (version is null || !Shape().IsMatch(version))
        {
            return false;
        }

        var text = version.Split('.');
        var parsed = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(text[i], out parsed[i]))
            {
                return false; // e.g. a part too large for an int
            }
        }

        parts = parsed;
        return true;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex Shape();
}
