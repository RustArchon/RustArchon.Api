// Copyright ©2026 Scott Blomfield

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.DiagnosticLogs;

public enum DiagnosticLogTokenState
{
    /// <summary>No token has ever been created: the feature is off.</summary>
    Off,

    Valid,

    /// <summary>Missing, malformed, wrong, replaced or revoked. Callers must not be able to tell which.</summary>
    Invalid,
}

public sealed record DiagnosticLogTokenCheck(DiagnosticLogTokenState State, string? TokenId);

/// <summary>What creating a token returns, once: <see cref="Token"/> is never retrievable afterwards.</summary>
public sealed record DiagnosticLogTokenCreated(string Token, string TokenId, DateTimeOffset CreatedAtUtc);

public sealed record DiagnosticLogTokenStatus(string TokenId, DateTimeOffset CreatedAtUtc, string CreatedBy, DateTimeOffset? RevokedAtUtc);

/// <summary>
/// The single credential for the diagnostic log endpoint (ADR-0006): created, rotated and revoked by a Site Admin, valid for nothing else.
/// </summary>
public interface IDiagnosticLogTokenService
{
    /// <summary>Makes a new token, replacing any existing one (which stops working at once). The secret is in the result and nowhere else.</summary>
    Task<DiagnosticLogTokenCreated> CreateAsync(string actor);

    /// <summary>What can be shown about the current token: never the secret. Null if none was ever created.</summary>
    Task<DiagnosticLogTokenStatus?> GetStatusAsync();

    /// <summary>Revokes the current token. False if there was none to revoke.</summary>
    Task<bool> RevokeAsync(string actor);

    Task<DiagnosticLogTokenCheck> CheckAsync(string? presented);
}

/// <summary>
/// Token shape: <c>&lt;8 hex id&gt;.&lt;43 characters, 256 bits of randomness&gt;</c>. The id is stored in the clear so use can be logged and a stale
/// token recognised; only a SHA-256 hash of the secret is kept (the secret is random enough that a plain hash is the right tool), inside the
/// <see cref="PlatformSettingsRegistry.DiagnosticLogToken"/> setting, encrypted at rest. Comparison is constant-time. A stored record that cannot
/// be read fails closed: nothing is valid.
/// </summary>
public sealed class DiagnosticLogTokenService(
    IPlatformSettingRepository settings, IApiKeyProtector protector, TimeProvider clock, ILogger<DiagnosticLogTokenService> logger)
    : IDiagnosticLogTokenService
{
    private const int MaxPresentedLength = 128;

    private static readonly Regex Shape = new("^(?<id>[0-9a-f]{8})\\.(?<secret>[A-Za-z0-9_-]{43})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Record(string Id, string Hash, DateTimeOffset CreatedUtc, string CreatedBy, DateTimeOffset? RevokedUtc);

    public async Task<DiagnosticLogTokenCreated> CreateAsync(string actor)
    {
        var row = await GetRowAsync();
        var id = RandomNumberGenerator.GetHexString(8, lowercase: true);
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();

        await SaveAsync(row, new Record(id, HashOf(secret), now, actor, null));
        logger.LogInformation("Diagnostic log token {TokenId} was created by {Actor}; any earlier token no longer works.", id, actor);
        return new DiagnosticLogTokenCreated(id + "." + secret, id, now);
    }

    public async Task<DiagnosticLogTokenStatus?> GetStatusAsync()
    {
        var record = Read(await GetRowAsync());
        return record is null ? null : new DiagnosticLogTokenStatus(record.Id, record.CreatedUtc, record.CreatedBy, record.RevokedUtc);
    }

    public async Task<bool> RevokeAsync(string actor)
    {
        var row = await GetRowAsync();
        var record = Read(row);
        if (record is null || record.RevokedUtc is not null)
        {
            return false;
        }

        await SaveAsync(row, record with { RevokedUtc = clock.GetUtcNow() });
        logger.LogInformation("Diagnostic log token {TokenId} was revoked by {Actor}.", record.Id, actor);
        return true;
    }

    public async Task<DiagnosticLogTokenCheck> CheckAsync(string? presented)
    {
        var row = await GetRowAsync();
        if (string.IsNullOrEmpty(row.Value))
        {
            return new DiagnosticLogTokenCheck(DiagnosticLogTokenState.Off, null);
        }

        var record = Read(row);
        var match = presented is { Length: > 0 and <= MaxPresentedLength } ? Shape.Match(presented) : null;

        // Hash whatever was presented even when it is malformed or there is nothing to compare, so the time taken does not tell the two apart.
        var secret = match is { Success: true } ? match.Groups["secret"].Value : string.Empty;
        var presentedHash = Convert.FromBase64String(HashOf(secret));

        if (record is null || match is not { Success: true } || record.RevokedUtc is not null
            || !string.Equals(match.Groups["id"].Value, record.Id, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(presentedHash, Convert.FromBase64String(record.Hash)))
        {
            return new DiagnosticLogTokenCheck(DiagnosticLogTokenState.Invalid, null);
        }

        return new DiagnosticLogTokenCheck(DiagnosticLogTokenState.Valid, record.Id);
    }

    private static string HashOf(string secret) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private async Task<PlatformSetting> GetRowAsync() =>
        await settings.GetByKeyAsync(PlatformSettingsRegistry.DiagnosticLogToken)
        ?? throw new InvalidOperationException(
            $"The '{PlatformSettingsRegistry.DiagnosticLogToken}' platform setting does not exist; it is created at startup by PlatformSettingsRegistry.");

    private async Task SaveAsync(PlatformSetting row, Record record)
    {
        row.Value = protector.Protect(ApiKeyProtectorPurposes.DiagnosticLogToken, JsonSerializer.Serialize(record));
        await settings.UpdateAsync(row);
    }

    private Record? Read(PlatformSetting row)
    {
        if (string.IsNullOrEmpty(row.Value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Record>(protector.Unprotect(ApiKeyProtectorPurposes.DiagnosticLogToken, row.Value));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            // Fail closed, and loudly: a record nobody can read must not become a reason to let anyone in.
            logger.LogError(ex, "The stored diagnostic log token record could not be read, so no token is accepted. Create a new token.");
            return null;
        }
    }
}
