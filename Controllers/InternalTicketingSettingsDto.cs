// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Controllers;

/// <summary>
/// The platform's current ticketing-integration configuration as returned by
/// <c>GET /internal/ticketing-settings</c> - the only place a decrypted webhook signing secret is ever
/// sent over the wire, to the one caller (RustArchon.Worker) authenticated via the internal shared
/// secret rather than a user/tenant JWT.
/// </summary>
/// <remarks>
/// Mirrors <c>RustArchon.Worker.Security.InternalTicketingSettings</c> exactly - see
/// <see cref="InternalEmailSettingsDto"/>'s own remarks for why this is a separate, duplicated type in
/// each project rather than a shared reference.
/// </remarks>
public record InternalTicketingSettingsDto(string Provider, string WebhookUrl, string WebhookSecret);
