// Copyright ©2026 Scott Blomfield

using System;

namespace RustArchon.Api.Services;

/// <summary>What the automatic third-party plugin updater has done lately: when it last ran, what it did, and when it next runs.</summary>
/// <param name="LastPassStartedUtc"><c>null</c> until a pass has run since this Api started.</param>
/// <param name="NextPassDueUtc"><c>null</c> until the schedule has been set.</param>
public sealed record ThirdPartyAutoUpdateSnapshot(
    DateTimeOffset? LastPassStartedUtc, DateTimeOffset? LastPassFinishedUtc, int ServersVisited, int UpdatesStarted, DateTimeOffset? NextPassDueUtc);

/// <summary>
/// Remembers, in memory, how the automatic updater's last pass went, so the Panel can answer "is it running, and when does it look at my server again?"
/// instead of that being visible only in the logs. In memory by choice (see change 002): after an Api restart it reads "has not run yet" with the next
/// time it is due, which is accurate for a process that has not run a pass.
/// </summary>
public sealed class ThirdPartyAutoUpdateStatus
{
    private readonly object _gate = new();
    private ThirdPartyAutoUpdateSnapshot _snapshot = new(null, null, 0, 0, null);

    public ThirdPartyAutoUpdateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void PassStarted(DateTimeOffset now)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { LastPassStartedUtc = now };
        }
    }

    public void PassFinished(DateTimeOffset now, int serversVisited, int updatesStarted)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { LastPassFinishedUtc = now, ServersVisited = serversVisited, UpdatesStarted = updatesStarted };
        }
    }

    public void NextDue(DateTimeOffset due)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { NextPassDueUtc = due };
        }
    }
}
