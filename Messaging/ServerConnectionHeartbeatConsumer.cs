// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Updates a server's worker-ownership/liveness columns. Not relayed to end users - see
/// <see cref="ServerConnectionHeartbeat"/>'s remarks for why this is a different signal from
/// <see cref="ConnectionStatusChanged"/>, which is.
/// </summary>
public class ServerConnectionHeartbeatConsumer(IRustServerRepository repository) : IConsumer<ServerConnectionHeartbeat>
{
    public async Task Consume(ConsumeContext<ServerConnectionHeartbeat> context)
    {
        var message = context.Message;

        var server = await repository.GetByIdAsync(message.ServerId, null);
        if (server is null)
        {
            // Deleted since the heartbeat was published - nothing to update.
            return;
        }

        server.AssignedWorkerId = message.WorkerId;
        server.LastHeartbeatUtc = message.AtUtc;
        await repository.UpdateAsync(server);
    }
}
