// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Services;

/// <summary>
/// Periodically re-publishes a <see cref="ConnectToServer"/> claim for any enabled server whose
/// heartbeat has gone stale, whether because its owning worker died outright or a single connection
/// hung inside an otherwise-healthy process. This is what makes the system self-healing - a crashed
/// worker's servers get picked up by a surviving instance within one sweep interval, uniformly,
/// without a separate "detect a dead worker" mechanism.
/// </summary>
/// <remarks>
/// The staleness threshold (45s) stays comfortably above <c>ConnectToServerConsumer</c>'s own
/// <c>FreshOwnershipWindow</c> (30s) - see that class's remarks - so a redundant sweep-published claim
/// never races a genuinely-still-healthy connection's own heartbeat cycle.
/// </remarks>
public class ServerClaimSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<ServerClaimSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StalenessThreshold = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // A single failed sweep (e.g. a transient DB blip) shouldn't kill the background
                // service - the next tick tries again.
                logger.LogError(ex, "Server claim sweep failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRustServerRepository>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var staleBefore = DateTimeOffset.UtcNow - StalenessThreshold;
        var servers = await repository.GetServersNeedingClaimAsync(staleBefore);

        foreach (var server in servers)
        {
            logger.LogDebug("Re-publishing claim for stale server {ServerId}", server.Id);
            await publishEndpoint.Publish(new ConnectToServer(server.Id, server.TenantId), cancellationToken);
        }
    }
}
