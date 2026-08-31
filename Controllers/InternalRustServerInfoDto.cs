// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Controllers;

/// <summary>
/// A server's connection details as returned by <c>GET /internal/rust-servers/{id}</c> - the only
/// place a decrypted RCON password is ever sent over the wire, to the one caller (RustArchon.Worker)
/// authenticated via the internal shared secret rather than a user/tenant JWT.
/// </summary>
/// <remarks>
/// Mirrors <c>RustArchon.Worker.Security.InternalRustServerInfo</c> exactly (same property names,
/// case-insensitive JSON matching on the Worker's deserialization side) - kept as a separate type in
/// each project, by original design, rather than a shared one in <c>RustArchon.Messaging</c>. Keep the
/// two in sync if either changes.
/// </remarks>
public record InternalRustServerInfoDto(
    Guid Id,
    Guid TenantId,
    string Host,
    int Port,
    string RconPassword,
    Guid? AssignedWorkerId,
    DateTimeOffset? LastHeartbeatUtc);
