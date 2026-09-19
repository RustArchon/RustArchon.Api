// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Cryptography;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Mints the random token behind <see cref="Data.Ticket.GuestAccessToken"/> - the same
/// <c>RandomNumberGenerator</c>-backed, URL-safe-Base64 shape JumpStart's own
/// <c>TenantInvitationService</c> uses for an invitation token, duplicated here rather than shared
/// since this Api project doesn't reference JumpStart's internal token helper directly.
/// </summary>
public static class GuestAccessTokenGenerator
{
    /// <summary>How long a freshly-issued or freshly-renewed token stays valid - see
    /// <see cref="Data.Ticket.GuestAccessTokenExpiresOn"/>'s remarks on why it's pushed forward on every
    /// notification rather than fixed at issuance.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
