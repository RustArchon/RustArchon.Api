// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Services;

/// <summary>What came of asking a server to be polled right now. See <see cref="RustArchon.Shared.DTOs.ServerPollResultDto"/>'s remarks for what each means to a caller.</summary>
public enum ServerPollOutcome
{
    Polled,
    NotConnected,
    NoWorker,
    RateLimited
}

/// <summary>
/// Asks the Worker instance that owns a server's connection to poll it right now, instead of waiting for its own few-minute schedule - see
/// <see cref="PollServerNow"/>'s remarks for why this exists and what it does and does not do.
/// </summary>
public interface IServerPollService
{
    /// <summary>Never throws: every way this can fail to happen (throttled, no worker, not connected) is a <see cref="ServerPollOutcome"/>, not an exception.</summary>
    Task<ServerPollOutcome> PollNowAsync(Guid serverId, IReadOnlyList<string> polls);
}

/// <inheritdoc />
public class ServerPollService(
    IRequestClient<PollServerNow> client, IServerPollThrottle throttle, ILogger<ServerPollService> logger) : IServerPollService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public async Task<ServerPollOutcome> PollNowAsync(Guid serverId, IReadOnlyList<string> polls)
    {
        if (!throttle.TryAcquire(serverId))
        {
            return ServerPollOutcome.RateLimited;
        }

        try
        {
            var response = await client.GetResponse<PollServerNowResult>(
                new PollServerNow(serverId, polls), timeout: RequestTimeout.After(s: (int)CommandTimeout.TotalSeconds));
            return response.Message.Connected ? ServerPollOutcome.Polled : ServerPollOutcome.NotConnected;
        }
        catch (RequestTimeoutException)
        {
            // Nobody answered: either no worker currently owns this server's connection, or the owning instance is stuck. Either way there is
            // nothing more specific to say - the throttle above already recorded the attempt, so this is not free to retry immediately either.
            logger.LogInformation("No worker answered a poll-now request for server {ServerId}.", serverId);
            return ServerPollOutcome.NoWorker;
        }
    }
}
