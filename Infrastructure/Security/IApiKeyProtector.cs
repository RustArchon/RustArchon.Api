// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.Security;

/// <summary>
/// Encrypts and decrypts third-party API keys (Steam Web API, a geolocation provider, ...) for
/// storage on <see cref="Data.RustServer"/>.
/// </summary>
/// <remarks>
/// A general-purpose sibling to <see cref="IRconCredentialProtector"/> rather than a third
/// near-identical copy of it - the two secret types this currently protects
/// (<see cref="Data.RustServer.SteamApiKey"/>, <see cref="Data.RustServer.GeolocationApiKey"/>) are
/// unrelated to each other and to the RCON password, so each gets its own <paramref name="purpose"/>
/// string (see <see cref="ApiKeyProtectorPurposes"/>) rather than sharing one - Data Protection's
/// purpose strings exist precisely so one kind of secret's key material can never be used to decrypt
/// another's.
/// </remarks>
public interface IApiKeyProtector
{
    /// <summary>Encrypts a plaintext API key for storage.</summary>
    /// <param name="purpose">One of <see cref="ApiKeyProtectorPurposes"/> - identifies which secret
    /// this is, so it can never be decrypted under a different purpose.</param>
    /// <param name="plaintextKey">The plaintext key as entered by the user.</param>
    string Protect(string purpose, string plaintextKey);

    /// <summary>Decrypts a stored API key back to plaintext, for use in an actual outbound request.</summary>
    /// <param name="purpose">Must match the purpose passed to <see cref="Protect"/> for this value.</param>
    /// <param name="protectedKey">The encrypted value from storage.</param>
    string Unprotect(string purpose, string protectedKey);
}

/// <summary>Purpose string constants for <see cref="IApiKeyProtector"/> - one per distinct secret type.</summary>
public static class ApiKeyProtectorPurposes
{
    public const string SteamApiKey = "RustArchon.RustServer.SteamApiKey.v1";
    public const string GeolocationApiKey = "RustArchon.RustServer.GeolocationApiKey.v1";
}
