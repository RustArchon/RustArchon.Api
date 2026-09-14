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
/// <remarks>
/// Uses <see cref="IRustServerRepository.GetByIdAcrossTenantsAsync"/>, not the ordinary tenant-scoped
/// <c>GetByIdAsync</c> - this is a MassTransit consumer with no ambient tenant (no <c>HttpContext</c>
/// for <c>JwtTenantContext</c> to read a claim from), and since ADR-018 made the tenant query filter
/// fail-closed, the tenant-scoped read always matched zero rows here. That silently took the
/// "server is null" branch on <em>every single heartbeat, for every server</em>, so <see cref="RustServer.AssignedWorkerId"/>/
/// <see cref="RustServer.LastHeartbeatUtc"/> never actually updated - confirmed live: both sat null
/// indefinitely on a server a Worker was demonstrably still actively retrying against. That in turn fed
/// <c>ServerClaimSweepService</c>/<see cref="IRustServerRepository.GetServersNeedingClaimAsync"/>, whose
/// whole definition of "needs claiming" is a null-or-stale heartbeat - with it permanently null, every
/// enabled server looked perpetually unclaimed, so the sweep kept re-publishing claims and tearing down/
/// recreating connection actors that were already fine, on top of whatever the server's own network was
/// doing. Same root cause, same fix shape, as <c>RustServerRepository.TryApplyConnectionStatusAsync</c>.
/// GetByIdAcrossTenantsAsync also excludes a disabled server, same as it does for a claim lookup - a
/// heartbeat for a server that's since been disabled shouldn't resurrect its liveness columns either.
/// </remarks>
public class ServerConnectionHeartbeatConsumer(IRustServerRepository repository) : IConsumer<ServerConnectionHeartbeat>
{
    public async Task Consume(ConsumeContext<ServerConnectionHeartbeat> context)
    {
        var message = context.Message;

        var server = await repository.GetByIdAcrossTenantsAsync(message.ServerId);
        if (server is null)
        {
            // Deleted, disabled, or otherwise gone since the heartbeat was published - nothing to
            // update.
            return;
        }

        server.AssignedWorkerId = message.WorkerId;
        server.LastHeartbeatUtc = message.AtUtc;
        await repository.UpdateAsync(server);
    }
}
