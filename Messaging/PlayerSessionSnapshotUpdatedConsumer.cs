// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Updates an open <see cref="Data.PlayerSession"/>'s "last known" ping/violation-level columns from a
/// fresh <c>playerlist</c> poll snapshot.
/// </summary>
/// <remarks>
/// No SignalR relay - unlike <see cref="PlayerConnectedConsumer"/>/<see cref="PlayerDisconnectedConsumer"/>,
/// this fires on every poll cycle for every already-known-connected player (a lot of messages over
/// time), and neither of the fields it updates has any live-facing UI yet - they exist purely so the
/// inactive-players view has "last known ping" once the session closes. Nothing to no-op if the
/// session already closed (e.g. this message was in flight when the disconnect landed) - the ordering
/// doesn't matter, since either landing order still ends with a sensible "last known" value.
/// </remarks>
public class PlayerSessionSnapshotUpdatedConsumer(IPlayerSessionRepository repository)
    : IConsumer<PlayerSessionSnapshotUpdated>
{
    public async Task Consume(ConsumeContext<PlayerSessionSnapshotUpdated> context)
    {
        var message = context.Message;

        var session = await repository.GetOpenSessionAsync(message.ServerId, message.SteamId);
        if (session is null)
        {
            return;
        }

        session.LastPing = message.Ping;
        session.LastViolationLevel = message.ViolationLevel;
        await repository.UpdateAsync(session);
    }
}
