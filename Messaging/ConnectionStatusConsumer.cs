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
    IConnectionLogRepository connectionLogRepository,
    IHubContext<RconHub> hubContext) : IConsumer<ConnectionStatusChanged>
{
    public async Task Consume(ConsumeContext<ConnectionStatusChanged> context)
    {
        var message = context.Message;

        // A single atomic UPDATE ... WHERE, not a read-then-write - status updates are published
        // fire-and-forget with no guaranteed delivery order, and this endpoint can process more than
        // one concurrently (no explicit concurrency limit is configured). Two transitions published
        // moments apart (e.g. Connecting immediately followed by Connected) can therefore be consumed
        // on two overlapping calls, each starting from its own independently-read snapshot - a plain
        // read-modify-write would let whichever one happens to save last win, regardless of which is
        // actually newer, which is exactly what left the UI stuck showing a stale status indefinitely
        // this session even though the connection itself was fine the whole time. See
        // TryApplyConnectionStatusAsync's remarks.
        var applied = await repository.TryApplyConnectionStatusAsync(
            message.ServerId, message.Status, message.Detail, message.ChangedAtUtc);
        if (!applied)
        {
            // Either the server no longer exists, or this transition is older than what's already
            // stored - either way, nothing to relay or log.
            return;
        }

        var level = LevelFor(message.Status);
        var logMessage = message.Detail ?? DefaultMessageFor(message.Status);

        // Appended unconditionally, even for a transition TryApplyConnectionStatusAsync's own
        // out-of-order guard would otherwise treat as "already applied" - it already passed that guard
        // to get here, so this is always a real, newly-observed transition worth keeping. Separate
        // from the RustServer row above on purpose: that row only ever holds the *current* status
        // (each write overwrites the last), while this is the append-only history a human actually
        // needs to answer "why did this drop earlier" after the fact - see ConnectionLogEntry's
        // remarks and the Logs tab.
        await connectionLogRepository.AddAsync(new ConnectionLogEntry
        {
            TenantId = message.TenantId,
            RustServerId = message.ServerId,
            Level = level,
            Message = logMessage,
            Status = message.Status,
            OccurredAtUtc = message.ChangedAtUtc
        });

        // ReceiveStatusChanged: the live badge feed (ServerDetail's header pill, ServersList's icons) -
        // unchanged. ReceiveLogEntry: a second, separate relay so the Logs tab's live stream sees this
        // transition alongside WorkerDiagnosticLoggedConsumer's own entries (see its remarks) without
        // needing to also understand the badge-specific event shape.
        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
            .SendAsync("ReceiveStatusChanged", message.ServerId, message.Status, message.Detail);
        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId))
            .SendAsync("ReceiveLogEntry", message.ServerId, level, logMessage, message.Status, message.ChangedAtUtc);
    }

    /// <summary>
    /// The Logs tab badge severity a given connection status implies - see <see cref="ConnectionLogLevel"/>'s
    /// remarks. <see cref="RconConnectionStatus.Connecting"/> and <see cref="RconConnectionStatus.Connected"/>
    /// are both routine, expected states, not anything to flag; the rest all mean something needs
    /// attention (already lost, mid-retry, or an outright failure).
    /// </summary>
    private static ConnectionLogLevel LevelFor(RconConnectionStatus status) => status switch
    {
        RconConnectionStatus.Connecting => ConnectionLogLevel.Info,
        RconConnectionStatus.Connected => ConnectionLogLevel.Info,
        RconConnectionStatus.Error => ConnectionLogLevel.Error,
        _ => ConnectionLogLevel.Warning // Reconnecting, Disconnected
    };

    /// <summary>
    /// Fallback log text for a transition published with no <see cref="ConnectionStatusChanged.Detail"/> -
    /// only <see cref="RconConnectionStatus.Connected"/> ever actually publishes with a null Detail in
    /// practice (see <c>ServerConnectionActor.OnConnectionChanged</c>), but every status is covered here
    /// so this can never leave <see cref="ConnectionLogEntry.Message"/> blank.
    /// </summary>
    private static string DefaultMessageFor(RconConnectionStatus status) => status switch
    {
        RconConnectionStatus.Connecting => "Connecting",
        RconConnectionStatus.Connected => "Connected",
        RconConnectionStatus.Reconnecting => "Reconnecting",
        RconConnectionStatus.Error => "Connection error",
        _ => "Disconnected"
    };
}
