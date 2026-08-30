// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;

namespace RustArchon.Api.Infrastructure.Security;

/// <summary>
/// Generates invitation codes for <see cref="Controllers.InvitationCodesController.Create"/>.
/// </summary>
public static class InvitationCodeGenerator
{
    // Excludes visually ambiguous characters (0/O, 1/I/L) so a code can be read aloud or retyped by
    // hand without confusion - these are meant to be pasted into a Discord invite or DM, not just
    // copy-pasted.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Generates a new 12-character random code, grouped as <c>XXXX-XXXX-XXXX</c> for readability.
    /// Cryptographically random - see <see cref="RandomNumberGenerator"/> - so guessing a valid code
    /// is infeasible regardless of how many codes are outstanding.
    /// </summary>
    public static string Generate()
    {
        var raw = RandomNumberGenerator.GetString(Alphabet, 12);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }
}
