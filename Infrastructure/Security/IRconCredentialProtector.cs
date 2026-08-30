// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.Security;

/// <summary>
/// Encrypts and decrypts RCON passwords for storage in <see cref="Data.RustServer.RconPassword"/>.
/// </summary>
/// <remarks>
/// RCON credentials must be recoverable in plaintext to actually open a connection to a Rust
/// server, so they are protected (reversibly encrypted), not hashed, using ASP.NET Core's Data
/// Protection APIs.
/// </remarks>
public interface IRconCredentialProtector
{
    /// <summary>
    /// Encrypts a plaintext RCON password for storage.
    /// </summary>
    /// <param name="plaintextPassword">The plaintext password as entered by the user.</param>
    /// <returns>The encrypted value to persist in <see cref="Data.RustServer.RconPassword"/>.</returns>
    string Protect(string plaintextPassword);

    /// <summary>
    /// Decrypts a stored RCON password back to plaintext, for use when opening an RCON connection.
    /// </summary>
    /// <param name="protectedPassword">The encrypted value from <see cref="Data.RustServer.RconPassword"/>.</param>
    /// <returns>The plaintext password.</returns>
    string Unprotect(string protectedPassword);
}
