// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Services;

/// <summary>
/// Owns a server's report-forwarding address: minting and rotating its secret, deciding whether a presented secret is good, and
/// checking what the game server itself is set to. See ADR-0001 for why the secret is in the address.
/// </summary>
public interface IReportForwardingService
{
    /// <summary>
    /// The server's forwarding address and state - minting the secret first if this is the first time anyone has asked. <c>null</c>
    /// if there is no such server among the caller's own (tenant-scoped).
    /// </summary>
    Task<ReportForwardingDto?> GetAsync(Guid serverId);

    /// <summary>Replaces the secret. The old address stops working at once. <c>null</c> if there is no such server.</summary>
    Task<ReportForwardingDto?> RotateAsync(Guid serverId);

    /// <summary>
    /// Asks the game server, over its console, what <c>server.reportsServerEndpoint</c> is set to and compares it to this server's
    /// address. <c>null</c> if there is no such server.
    /// </summary>
    Task<VerifyReportForwardingResultDto?> VerifyAsync(Guid serverId);

    /// <summary>
    /// Whether <paramref name="token"/> is the current secret for <paramref name="serverId"/>. Fails closed: a server that does
    /// not exist, is disabled, or has no secret yet all answer <c>false</c>, and take the same time to do it as a wrong secret.
    /// Crosses the tenant boundary on purpose - the caller is a game server, not a user.
    /// </summary>
    Task<bool> IsTokenValidAsync(Guid serverId, string token);
}

/// <inheritdoc />
public partial class ReportForwardingService(
    IRustServerRepository servers,
    IServerReportRepository reports,
    IApiKeyProtector protector,
    IPlatformSettingsCache settings,
    IRequestClient<SendRconCommand> rcon,
    TimeProvider clock,
    ILogger<ReportForwardingService> logger) : IReportForwardingService
{
    /// <summary>The convar the game server is told to forward to. Read back as well as set.</summary>
    public const string EndpointConvar = "server.reportsServerEndpoint";

    /// <summary>A real secret is 43 characters (32 random bytes, base64url). Anything longer is not even looked at.</summary>
    public const int MaxTokenLength = 128;

    // What a wrong-length or unknown-server attempt is compared against, so it costs what a real comparison costs.
    private static readonly byte[] DummySecret = Encoding.UTF8.GetBytes(new string('x', 43));

    /// <inheritdoc />
    public async Task<ReportForwardingDto?> GetAsync(Guid serverId)
    {
        var server = await servers.GetByIdAsync(serverId, null);
        if (server is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(server.ReportsSecret))
        {
            await MintAsync(server);
        }

        return await ToDtoAsync(server);
    }

    /// <inheritdoc />
    public async Task<ReportForwardingDto?> RotateAsync(Guid serverId)
    {
        var server = await servers.GetByIdAsync(serverId, null);
        if (server is null)
        {
            return null;
        }

        await MintAsync(server);
        logger.LogInformation("Rotated the report-forwarding secret for server {ServerId}.", serverId);
        return await ToDtoAsync(server);
    }

    /// <inheritdoc />
    public async Task<VerifyReportForwardingResultDto?> VerifyAsync(Guid serverId)
    {
        var server = await servers.GetByIdAsync(serverId, null);
        if (server is null)
        {
            return null;
        }

        // A server nobody has asked the address of has nothing to compare against - and must not have one minted as a side effect
        // of a check, so the answer is simply "not set the way we would tell it to be".
        if (string.IsNullOrEmpty(server.ReportsSecret))
        {
            return new VerifyReportForwardingResultDto { Verdict = ReportForwardingVerdict.NotSet };
        }

        var expected = await BuildUrlAsync(server);

        RconCommandResult? reply;
        try
        {
            // Non-interactive on purpose: it is a backend check, not something a person typed, so it never shows on a tenant's
            // Console tab (only a site admin acting as the tenant sees non-interactive frames). The reply contains the secret.
            var response = await rcon.GetResponse<RconCommandResult>(
                new SendRconCommand(serverId, EndpointConvar, Interactive: false),
                timeout: RequestTimeout.After(s: 10));
            reply = response.Message;
        }
        catch (RequestTimeoutException)
        {
            return new VerifyReportForwardingResultDto { Verdict = ReportForwardingVerdict.Unavailable };
        }

        if (!reply.Success)
        {
            return new VerifyReportForwardingResultDto { Verdict = ReportForwardingVerdict.Unavailable };
        }

        var result = Interpret(reply.Message, expected);
        if (result.Verdict == ReportForwardingVerdict.Matches)
        {
            server.ReportForwardingVerifiedAtUtc = clock.GetUtcNow();
            await servers.UpdateAsync(server);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsTokenValidAsync(Guid serverId, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        var server = await servers.GetByIdAcrossTenantsAsync(serverId);
        byte[] real = DummySecret;
        var usable = false;
        if (server is { ReportsSecret: { Length: > 0 } stored })
        {
            try
            {
                real = Encoding.UTF8.GetBytes(protector.Unprotect(ApiKeyProtectorPurposes.ReportsSecret, stored));
                usable = true;
            }
            catch (CryptographicException ex)
            {
                // A stored value that no longer decrypts (a lost key ring) is a closed door, not an open one.
                logger.LogWarning(ex, "The report-forwarding secret of server {ServerId} could not be decrypted; refusing its reports.", serverId);
            }
        }

        var equal = CryptographicOperations.FixedTimeEquals(real, Encoding.UTF8.GetBytes(token));
        return usable && equal;
    }

    /// <summary>
    /// Works out what the game server's reply to reading the convar means. Fails closed: only a reply that contains exactly the
    /// expected address is <see cref="ReportForwardingVerdict.Matches"/>.
    /// </summary>
    public static VerifyReportForwardingResultDto Interpret(string? reply, string expectedUrl)
    {
        var text = reply ?? string.Empty;
        string? firstObserved = null;

        foreach (Match candidate in UrlPattern().Matches(text))
        {
            var url = candidate.Value.TrimEnd('.', ',', ';', ')');
            if (string.Equals(url, expectedUrl, StringComparison.Ordinal))
            {
                return new VerifyReportForwardingResultDto
                {
                    Verdict = ReportForwardingVerdict.Matches,
                    ObservedRedacted = Redact(url)
                };
            }

            firstObserved ??= url;
        }

        if (firstObserved is not null)
        {
            return new VerifyReportForwardingResultDto
            {
                Verdict = ReportForwardingVerdict.Mismatch,
                ObservedRedacted = Redact(firstObserved)
            };
        }

        // The console prints an unset string convar as an empty pair of quotes.
        return new VerifyReportForwardingResultDto
        {
            Verdict = text.Contains("\"\"", StringComparison.Ordinal)
                ? ReportForwardingVerdict.NotSet
                : ReportForwardingVerdict.Unreadable
        };
    }

    /// <summary>
    /// The address with its secret removed, so it can be shown ("your server points at ...") without being a leak. The secret is
    /// always the last path segment, so that is what goes.
    /// </summary>
    public static string Redact(string url)
    {
        var cut = url.LastIndexOf('/');
        return cut > 0 && cut < url.Length - 1 ? string.Concat(url.AsSpan(0, cut + 1), "***") : url;
    }

    /// <summary>The convar line an admin pastes, for an address.</summary>
    public static string CommandFor(string url) => $"{EndpointConvar} \"{url}\"";

    private async Task MintAsync(RustServer server)
    {
        var secret = NewSecret();
        server.ReportsSecret = protector.Protect(ApiKeyProtectorPurposes.ReportsSecret, secret);

        // The old address no longer counts as configured, so a previous "verified" no longer means anything.
        server.ReportForwardingVerifiedAtUtc = null;
        await servers.UpdateAsync(server);
    }

    private async Task<ReportForwardingDto> ToDtoAsync(RustServer server)
    {
        var url = await BuildUrlAsync(server);
        return new ReportForwardingDto
        {
            Url = url,
            Command = CommandFor(url),
            Verified = server.ReportForwardingVerifiedAtUtc is not null,
            VerifiedAtUtc = server.ReportForwardingVerifiedAtUtc,
            LastReportReceivedAtUtc = await reports.GetLastNativeReceivedAsync(server.Id)
        };
    }

    private async Task<string> BuildUrlAsync(RustServer server)
    {
        var configured = await settings.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl);
        var baseUrl = (configured is { Length: > 0 } ? configured : PlatformSettingsRegistry.DefaultPanelBaseUrl).TrimEnd('/');
        var secret = protector.Unprotect(ApiKeyProtectorPurposes.ReportsSecret, server.ReportsSecret!);
        return $"{baseUrl}/ingest/reports/{server.Id}/{secret}";
    }

    /// <summary>A fresh 256-bit secret, base64url so it is safe in an address.</summary>
    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex(@"https?://[^\s""']+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
