// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using AutoMapper;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Infrastructure.Geolocation;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Infrastructure.Steam;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Opens a new <see cref="PlayerSession"/> for a just-detected connect, relays it to any Blazor client
/// watching that server, then best-effort enriches it with geolocation and Steam account info.
/// </summary>
/// <remarks>
/// Both lookups happen after the session is already persisted and relayed - either being slow or
/// unconfigured only delays the enrichment columns filling in later, never the connect event itself
/// from showing up. Both are driven entirely by the owning <see cref="RustServer"/>'s own settings
/// (<see cref="RustServer.GeolocationProvider"/>/<see cref="RustServer.GeolocationApiKey"/>,
/// <see cref="RustServer.SteamApiKey"/>) - there is no global fallback configuration anymore. No
/// ambient tenant context here - see <c>ConnectionStatusConsumer</c>'s remarks for why
/// <c>GetByIdAsync</c>-style tenant scoping still works correctly from a consumer regardless.
/// </remarks>
public class PlayerConnectedConsumer(
    IPlayerSessionRepository repository,
    IRustServerRepository rustServerRepository,
    IGeolocationService geolocationService,
    ISteamApiClient steamApiClient,
    IApiKeyProtector apiKeyProtector,
    IMapper mapper,
    IHubContext<RconHub> hubContext,
    ILogger<PlayerConnectedConsumer> logger) : IConsumer<PlayerConnected>
{
    public async Task Consume(ConsumeContext<PlayerConnected> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        // Self-healing for a real, observed case: a Worker restart mid-session has no memory of who
        // was already connected, so its next reconciliation poll reports them as a fresh connect (see
        // ServerConnectionActor's remarks) even though they never actually left. Left unhandled, that
        // orphans the previous session open forever (the eventual real disconnect only closes the most
        // recent one) and the same player shows up more than once in "currently connected." Closing
        // any stale leftovers first, right as the new session opens, keeps that self-correcting instead
        // of accumulating - a duplicate here is a display artifact, not a data-integrity concern this
        // needs to be perfectly precise about.
        var staleOpenSessions = await repository.GetOpenSessionsAsync(message.ServerId, message.SteamId);
        foreach (var stale in staleOpenSessions)
        {
            stale.DisconnectedAtUtc = message.ConnectedAtUtc;
            await repository.UpdateAsync(stale);
        }

        var session = await repository.AddAsync(new PlayerSession
        {
            TenantId = message.TenantId,
            RustServerId = message.ServerId,
            SteamId = message.SteamId,
            DisplayName = message.DisplayName,
            IpAddress = message.IpAddress,
            ConnectedAtUtc = message.ConnectedAtUtc
        });

        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
            .SendAsync("ReceivePlayerConnected", mapper.Map<PlayerSessionDto>(session));

        // The server's own settings decide what (if anything) gets looked up - both null-check
        // cleanly (GeolocationService/SteamApiClient both treat "no key" as "nothing to report," not
        // an error), so there's no need to branch on "is this configured" before calling either.
        var server = await rustServerRepository.GetByIdAsync(message.ServerId, null);
        if (server is null)
        {
            // Deleted between the connect happening and this consumer running - nothing left to
            // enrich or relay.
            return;
        }

        var enriched = false;

        try
        {
            var geolocationApiKey = server.GeolocationApiKey is { Length: > 0 } encryptedGeoKey
                ? apiKeyProtector.Unprotect(ApiKeyProtectorPurposes.GeolocationApiKey, encryptedGeoKey)
                : null;
            var geo = await geolocationService.LookupAsync(
                server.GeolocationProvider, geolocationApiKey, message.IpAddress, cancellationToken);
            if (geo is not null)
            {
                session.GeolocationProvider = geo.Provider;
                session.GeolocationCountry = geo.Country;
                session.GeolocationCountryCode = geo.CountryCode;
                session.GeolocationIsVpn = geo.IsVpn;
                session.GeolocationIsProxy = geo.IsProxy;
                session.GeolocationCheckedAtUtc = DateTimeOffset.UtcNow;
                enriched = true;
            }
        }
        catch (Exception ex)
        {
            // Enrichment failing shouldn't fail the whole message - the session already exists with
            // its geolocation columns left null, exactly as if none were configured at all.
            logger.LogWarning(ex, "Geolocation lookup failed for {IpAddress}", message.IpAddress);
        }

        try
        {
            if (server.SteamApiKey is { Length: > 0 } encryptedSteamKey)
            {
                var steamApiKey = apiKeyProtector.Unprotect(ApiKeyProtectorPurposes.SteamApiKey, encryptedSteamKey);
                var steamInfo = await steamApiClient.GetPlayerInfoAsync(steamApiKey, message.SteamId, cancellationToken);
                if (steamInfo is not null)
                {
                    session.SteamVacBanned = steamInfo.VacBanned;
                    session.SteamNumberOfVacBans = steamInfo.NumberOfVacBans;
                    session.SteamNumberOfGameBans = steamInfo.NumberOfGameBans;
                    session.SteamMinutesPlayedForever = steamInfo.MinutesPlayedForever;
                    session.SteamInfoCheckedAtUtc = DateTimeOffset.UtcNow;
                    enriched = true;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Steam lookup failed for {SteamId}", message.SteamId);
        }

        if (enriched)
        {
            await repository.UpdateAsync(session);

            // One combined relay for either kind of enrichment landing, not a separate event per
            // source - a client watching "currently connected" just needs the row refreshed, not to
            // know which specific lookup caused it.
            await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
                .SendAsync("ReceivePlayerGeolocated", mapper.Map<PlayerSessionDto>(session));
        }
    }
}
