// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;
using JumpStart.Data.Auditing;
using JumpStart.Data.MultiTenant;
using Microsoft.EntityFrameworkCore;

namespace RustArchon.Api.Data;

/// <summary>
/// Represents a Rust game server a tenant has registered for RCON management.
/// </summary>
/// <remarks>
/// Tenant-scoped (<see cref="ITenantScoped"/>) so one tenant's servers are never visible to another
/// - see JumpStart's multi-tenancy documentation. <see cref="RconPassword"/> is always stored
/// encrypted via <see cref="Infrastructure.Security.IRconCredentialProtector"/>, applied in
/// <see cref="Controllers.RustServersController"/>; it is never exposed through the API - see
/// <c>RustArchon.Shared.DTOs.RustServerDto</c>.
/// </remarks>
[Table("RustServer")]
[Index(nameof(TenantId), nameof(Name), IsUnique = true, Name = "IX_RustServer_TenantId_Name")]
public class RustServer : AuditableNamedEntity, ITenantScoped
{
    /// <summary>
    /// Gets or sets the unique identifier of the tenant that owns this server.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Gets or sets the tenant that owns this server.
    /// </summary>
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Gets or sets the hostname or IP address the RCON connection is made to.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the RCON port. Rust's default RCON port is 28016.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 28016;

    /// <summary>
    /// Gets or sets the RCON password, encrypted at rest. Set only via
    /// <see cref="Infrastructure.Security.IRconCredentialProtector.Protect"/> - never store plaintext here.
    /// </summary>
    /// <remarks>
    /// No explicit <c>[Column(TypeName = ...)]</c> - an unbounded <see cref="string"/> property with
    /// no <c>[MaxLength]</c> already maps to each provider's own "unlimited text" type by convention
    /// (<c>text</c> on PostgreSQL, <c>nvarchar(max)</c> on SQL Server), which stays portable across
    /// providers instead of hard-coding one provider's SQL type name.
    /// </remarks>
    [Required]
    public string RconPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional free-text description of this server.
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }
}
