// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// A person's own settings - what they prefer, as the application (not the sign-in system) knows it. One row per person at most, keyed by the identity
/// user's id; no row means every setting is at its default.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists rather than a column on the identity user: the identity database is for signing in, lives in the Panel, and is out of the Api's
/// reach - the Api knows people only by id. A setting the Api has to act on (the language an announcement is written in, say) has to be somewhere the Api
/// can read it, and settings added later belong beside this one and not on identity. The identity user used to carry the language itself; the Panel's
/// <c>LegacyUserCulture</c> hand-over moved what it held here and the column is gone.
/// </para>
/// <para>
/// Not tenant-scoped: a person's settings are theirs, whichever organizations they belong to. Typed columns rather than a key/value bag, so a setting
/// has a type and a default in one place; adding one is a column.
/// </para>
/// </remarks>
[Table("UserProfile")]
[Index(nameof(UserId), IsUnique = true, Name = "IX_UserProfile_UserId")]
public class UserProfile : Entity
{
    /// <summary>The identity user this belongs to. There is no foreign key: the user lives in another database.</summary>
    public Guid UserId { get; set; }

    /// <summary>The language (e.g. <c>en-US</c>) the person reads, or <c>null</c> if they have not chosen one.</summary>
    [MaxLength(35)]
    public string? PreferredCulture { get; set; }

    public DateTimeOffset UpdatedOn { get; set; }
}
