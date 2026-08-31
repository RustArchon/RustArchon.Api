// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using RustArchon.Api.Hubs;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Updates a server's live connection-status columns and relays the change to any Blazor client
/// currently watching it.
/// </summary>
/// <remarks>
/// Uses <see cref="IRustServerRepository.GetByIdAsync"/>/<c>UpdateAsync</c> directly, not
/// <see cref="IRustServerRepository.GetByIdAcrossTenantsAsync"/> - a consumer has no ambient tenant
/// context (no <c>HttpContext</c> for <c>JwtTenantContext</c> to read a claim from), and
/// <c>JumpStartDbContext</c>'s tenant query filter is a documented no-op whenever the ambient tenant
/// is null, so every tenant's rows are already visible here without needing <c>IgnoreQueryFilters()</c>.
/// </remarks>
public class ConnectionStatusConsumer(
    IRustServerRepository repository,
    IHubContext<RconHub> hubContext) : IConsumer<ConnectionStatusChanged>
{
    public async Task Consume(ConsumeContext<ConnectionStatusChanged> context)
    {
        var message = context.Message;

        var server = await repository.GetByIdAsync(message.ServerId, null);
        if (server is null)
        {
            // Deleted since the status change was published - nothing to update or relay.
            return;
        }

        server.ConnectionStatus = message.Status;
        server.ConnectionStatusDetail = message.Detail;
        server.ConnectionStatusChangedAtUtc = message.ChangedAtUtc;
        await repository.UpdateAsync(server);

        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
            .SendAsync("ReceiveStatusChanged", message.ServerId, message.Status, message.Detail);
    }
}
