// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Hubs;

/// <summary>
/// Live-tails a server's captured console/chat/status to whichever Blazor clients are viewing it.
/// </summary>
/// <remarks>
/// Server-to-client push happens entirely from the MassTransit consumers
/// (<c>RconFrameIngestionConsumer</c>, <c>ConnectionStatusConsumer</c>) via <see cref="IHubContext{RconHub}"/> -
/// never from inside this class itself, which only ever handles a client joining or leaving a
/// server's group. Authenticated on the same JWT bearer scheme as every REST endpoint - see
/// <c>Program.cs</c>'s <c>OnMessageReceived</c> addition, needed because browsers can't set an
/// <c>Authorization</c> header on a WebSocket handshake.
/// </remarks>
[Authorize]
public class RconHub : Hub
{
    private readonly IRustServerRepository _rustServerRepository;

    public RconHub(IRustServerRepository rustServerRepository)
    {
        _rustServerRepository = rustServerRepository ?? throw new ArgumentNullException(nameof(rustServerRepository));
    }

    /// <summary>
    /// Joins the group for one server's live events, after confirming the caller's tenant can
    /// actually see it - reuses <see cref="IRustServerRepository.GetByIdAsync"/>'s existing
    /// tenant-scoping rather than re-implementing an access check here.
    /// </summary>
    public async Task JoinServerGroup(Guid serverId)
    {
        var server = await _rustServerRepository.GetByIdAsync(serverId, null);
        if (server is null)
        {
            throw new HubException("Server not found, or it's not one of yours.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(serverId));
    }

    public async Task LeaveServerGroup(Guid serverId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(serverId));
    }

    /// <summary>
    /// The SignalR group name for one server's events - shared with the consumers that push into it.
    /// </summary>
    public static string GroupName(Guid serverId) => $"server-{serverId}";
}
