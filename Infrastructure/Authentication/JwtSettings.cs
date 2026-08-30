// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.Authentication;

/// <summary>
/// Configuration settings for JWT authentication in the Web API. Bound from the
/// <c>JwtSettings</c> section of <c>appsettings.json</c>.
/// </summary>
public class JwtSettings
{
    /// <summary>
    /// Gets or sets the secret key used for signing JWT tokens. Must be at least 32 characters and
    /// identical to the value configured in the RustArchon Blazor project. Store this securely
    /// (environment variable, user-secrets, or a secrets manager) in production - never commit a
    /// production value to source control.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the issuer of the JWT token.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the audience for the JWT token.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token expiration time in minutes. Default is 60 minutes.
    /// </summary>
    public int ExpirationMinutes { get; set; } = 60;
}
