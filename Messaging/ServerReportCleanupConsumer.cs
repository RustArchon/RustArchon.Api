// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Removes a deleted server's reports and their screenshots. Runs off <see cref="ServerLifecycleChanged"/> rather than inside the
/// delete endpoint so that <c>RustServersController</c> does not have to know about reports at all.
/// </summary>
/// <remarks>
/// A report names players and can carry a picture of someone's screen; it has no business outliving the server it was about. The
/// consumer has no ambient tenant (it is a bus message), so the repository call is by server id, which is exactly what makes it
/// safe: it only ever touches the server that message named.
/// </remarks>
public class ServerReportCleanupConsumer(
    IServerReportRepository reports,
    IObjectStorage storage,
    ILogger<ServerReportCleanupConsumer> logger) : IConsumer<ServerLifecycleChanged>
{
    public async Task Consume(ConsumeContext<ServerLifecycleChanged> context)
    {
        var message = context.Message;
        if (message.ChangeType != ServerLifecycleChangeType.Deleted)
        {
            return;
        }

        var removed = await reports.DeleteForServerAcrossTenantsAsync(message.ServerId);
        await storage.DeleteByPrefixAsync($"reports/{message.ServerId}/", context.CancellationToken);

        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} report(s) and their screenshots for deleted server {ServerId}.", removed, message.ServerId);
        }
    }
}
