// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using AutoMapper;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Persists a detected kill and relays it to any Blazor client watching that server. See
/// <see cref="PlayerKilled"/>'s remarks for why this is heuristic, not authoritative.
/// </summary>
public class PlayerKilledConsumer(
    IPlayerKillEventRepository repository,
    IMapper mapper,
    IHubContext<RconHub> hubContext) : IConsumer<PlayerKilled>
{
    public async Task Consume(ConsumeContext<PlayerKilled> context)
    {
        var message = context.Message;

        var kill = await repository.AddAsync(new PlayerKillEvent
        {
            TenantId = message.TenantId,
            RustServerId = message.ServerId,
            OccurredAtUtc = message.OccurredAtUtc,
            VictimName = message.VictimName,
            VictimSteamId = message.VictimSteamId,
            KillerName = message.KillerName,
            KillerSteamId = message.KillerSteamId,
            Weapon = message.Weapon,
            RawMessage = message.RawMessage
        });

        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
            .SendAsync("ReceivePlayerKilled", mapper.Map<PlayerKillEventDto>(kill));
    }
}
