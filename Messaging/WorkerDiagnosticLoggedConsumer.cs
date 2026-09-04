// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Persists a worker-side diagnostic event (a parse error, a poll failure, ...) to the Logs tab and
/// relays it to any Blazor client currently watching that server - the counterpart to
/// <see cref="ConnectionStatusConsumer"/> for the entries that aren't themselves a connection-status
/// transition. See <see cref="WorkerDiagnosticLogged"/>'s remarks for why these are two separate
/// message types landing in the same <see cref="ConnectionLogEntry"/> table.
/// </summary>
public class WorkerDiagnosticLoggedConsumer(
    IConnectionLogRepository connectionLogRepository,
    IHubContext<RconHub> hubContext) : IConsumer<WorkerDiagnosticLogged>
{
    public async Task Consume(ConsumeContext<WorkerDiagnosticLogged> context)
    {
        var message = context.Message;

        // No out-of-order guard here, unlike ConnectionStatusConsumer's TryApplyConnectionStatusAsync -
        // that guard exists to protect a *current-value* column from a stale write; this only ever
        // appends, so there's no "already applied" state to protect and every message is worth keeping
        // regardless of delivery order.
        await connectionLogRepository.AddAsync(new ConnectionLogEntry
        {
            TenantId = message.TenantId,
            RustServerId = message.ServerId,
            Level = message.Level,
            Message = message.Message,
            Status = null,
            OccurredAtUtc = message.OccurredAtUtc
        });

        // No ReceiveStatusChanged relay - a diagnostic entry never implies a connection-status change,
        // so it has nothing to tell the live badge feed. Only the Logs tab's stream needs this.
        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
            .SendAsync("ReceiveLogEntry", message.ServerId, message.Level, message.Message, (RconConnectionStatus?)null, message.OccurredAtUtc);
    }
}
