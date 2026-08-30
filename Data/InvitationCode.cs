// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data.Auditing;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A single-use code that gates account registration during the soft launch. See
/// <see cref="Controllers.InvitationsController"/> (anonymous redemption, used by the Register page
/// before an account exists) and <see cref="Controllers.InvitationCodesController"/> (platform-admin
/// management: minting and deactivating codes).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>ITenantScoped</c> - a code gates the creation of a brand-new tenant, so it
/// can't belong to one. It's a platform-wide concept, the same way <c>Tenant</c> itself is.
/// </para>
/// <para>
/// <see cref="RedeemedAtUtc"/>/<see cref="RedeemedByEmail"/> are written exactly once, atomically, by
/// <see cref="Repositories.IInvitationCodeRepository.TryRedeemAsync"/> - never through the normal
/// <c>UpdateAsync</c> path, which is why the update DTO/mapping profile ignore them entirely (only
/// <see cref="Note"/> and <see cref="IsActive"/> are editable after creation).
/// </para>
/// </remarks>
[Table("InvitationCode")]
[Index(nameof(Code), IsUnique = true, Name = "IX_InvitationCode_Code")]
public class InvitationCode : AuditableEntity
{
    /// <summary>
    /// Gets or sets the code itself, generated server-side by
    /// <see cref="Infrastructure.Security.InvitationCodeGenerator"/> - never client-supplied.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional admin-facing label (e.g. "Discord mods batch"), never shown to the
    /// person redeeming the code.
    /// </summary>
    [MaxLength(200)]
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets the email address this code is restricted to, or <c>null</c> if it may be
    /// redeemed by whoever submits it first. Always stored lower-cased/trimmed.
    /// </summary>
    [MaxLength(256)]
    public string? BoundEmail { get; set; }

    /// <summary>
    /// Gets or sets whether this code can still be redeemed. Set to <c>false</c> to revoke a code
    /// before it's used, independent of the platform-wide <c>RUSTARCHON_INVITATION_CODES_ENABLED</c>
    /// kill switch (see <see cref="Infrastructure.InvitationCodeOptions"/>) that turns the whole gate off.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Gets or sets when this code was redeemed, or <c>null</c> if it hasn't been yet.
    /// </summary>
    public DateTimeOffset? RedeemedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the email address that redeemed this code, or <c>null</c> if it hasn't been yet.
    /// </summary>
    [MaxLength(256)]
    public string? RedeemedByEmail { get; set; }
}
