// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Administration;

/// <summary>
/// Acting on another Organization's servers, as a site admin - the support half of the Organizations
/// console.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every one of these already exists on <c>RustServersController</c>, scoped to the caller's own
/// tenant.</strong> They are duplicated here rather than having that controller relax its scoping,
/// because those two audiences want opposite defaults: an Organization member must never reach a server
/// that is not theirs, and that guarantee is worth more than the handful of lines saved by making the
/// tenant check conditional. A conditional boundary is one bad `if` away from not being a boundary.
/// </para>
/// <para>
/// <strong>The messages published are identical.</strong> A worker cannot tell whether a disable came
/// from the customer or from support, and should not: the connection teardown, the claim sweep and the
/// lifecycle handling are the same job either way. Publishing a different contract for the admin path
/// would mean two code paths in the Worker for one outcome.
/// </para>
/// <para>
/// Deliberately no delete. Support can disable a server, which stops it being connected and is
/// reversible; removing someone's server record - and the history hanging off it - is the customer's
/// decision to make.
/// </para>
/// </remarks>
public interface IOrganizationServerAdminService
{
    /// <summary>
    /// Enables a server and publishes a fresh <see cref="ConnectToServer"/> claim.
    /// </summary>
    /// <returns><c>false</c> when no such server belongs to that Organization.</returns>
    Task<bool> EnableAsync(Guid tenantId, Guid serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables a server, publishing the lifecycle change that tears its connection down.
    /// </summary>
    Task<bool> DisableAsync(Guid tenantId, Guid serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the connection details support most often has to correct - the host, the port, and the
    /// RCON password.
    /// </summary>
    /// <remarks>
    /// A blank <paramref name="rconPassword"/> leaves the stored one alone, matching the tenant-facing
    /// edit: the password is write-only everywhere, and "unchanged" has to be expressible without the
    /// caller ever having been shown it.
    /// </remarks>
    Task<bool> UpdateConnectionAsync(
        Guid tenantId, Guid serverId, string host, int port, string? rconPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an RCON command to a server on the Organization's behalf.
    /// </summary>
    /// <returns>
    /// The command's result, or <c>null</c> when no such server belongs to that Organization. A server
    /// whose connection is not live comes back as an unsuccessful result rather than as null.
    /// </returns>
    Task<RconCommandResult?> SendCommandAsync(
        Guid tenantId, Guid serverId, string command, CancellationToken cancellationToken = default);
}
