// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using RustArchon.Api.Hubs;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Closes out the open <see cref="Data.PlayerSession"/> for a just-detected disconnect and relays it
/// to any Blazor client watching that server.
/// </summary>
public class PlayerDisconnectedConsumer(
    IPlayerSessionRepository repository,
    IHubContext<RconHub> hubContext) : IConsumer<PlayerDisconnected>
{
    public async Task Consume(ConsumeContext<PlayerDisconnected> context)
    {
        var message = context.Message;

        var session = await repository.GetOpenSessionAsync(message.ServerId, message.SteamId);
        if (session is null)
        {
            // No open session to close - e.g. this worker instance restarted and lost its in-memory
            // "who was here last poll" state right as the player left. Nothing to update or relay.
            return;
        }

        session.DisconnectedAtUtc = message.DisconnectedAtUtc;
        await repository.UpdateAsync(session);

        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
            .SendAsync("ReceivePlayerDisconnected", message.SteamId);
    }
}
