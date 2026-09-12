// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Controllers;

/// <summary>
/// The platform's current email delivery configuration as returned by
/// <c>GET /internal/email-settings</c> - the only place a decrypted SMTP password or  API key
/// is ever sent over the wire, to the one caller (RustArchon.Worker) authenticated via the internal
/// shared secret rather than a user/tenant JWT.
/// </summary>
/// <remarks>
/// Mirrors <c>RustArchon.Worker.Security.InternalEmailSettings</c> exactly (same property names,
/// case-insensitive JSON matching on the Worker's deserialization side) - kept as a separate type in
/// each project, by original design, the same way <c>InternalRustServerInfoDto</c>/
/// <c>InternalRustServerInfo</c> are. Keep the two in sync if either changes.
/// </remarks>
public record InternalEmailSettingsDto(
    string ServiceProvider,
    string SmtpHost,
    int SmtpPort,
    bool SmtpEnableSsl,
    string SmtpUsername,
    string SmtpPassword,
    string ApiKey,
    string DefaultFromAddress,
    string DefaultFromName);
