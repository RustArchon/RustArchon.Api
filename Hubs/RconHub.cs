// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Services.Authentication.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Hubs;

/// <summary>
/// Live-tails a server's captured console/chat/status to whichever Blazor clients are viewing it.
/// </summary>
/// <remarks>
/// <para>
/// Server-to-client push happens entirely from the MassTransit consumers
/// (<c>RconFrameIngestionConsumer</c>, <c>ConnectionStatusConsumer</c>) via <see cref="IHubContext{RconHub}"/> -
/// never from inside this class itself, which only ever handles a client joining or leaving a
/// server's group. Authenticated on the same JWT bearer scheme as every REST endpoint - see
/// <c>Program.cs</c>'s <c>OnMessageReceived</c> addition, needed because browsers can't set an
/// <c>Authorization</c> header on a WebSocket handshake.
/// </para>
/// <para>
/// <strong>Two groups per server, not one.</strong> <see cref="GroupName"/> only ever receives
/// interactive events - anyone who can see the server can join it, same as before.
/// <see cref="UnfilteredGroupName"/> additionally receives non-interactive (background/potentially
/// privileged) events, and <see cref="JoinUnfilteredServerGroup"/> only admits a caller whose token
/// carries <see cref="TokenController.ActingAsClaimType"/> - i.e. a site admin currently acting as
/// this tenant (see <c>SiteAdminCrossTenantPolicy</c>), never an ordinary tenant member regardless of
/// their own permissions. This is what makes the filtering actually server-side: a non-interactive
/// event is never sent down a connection that hasn't passed this check, rather than being sent to
/// everyone and merely left unrendered by client code that could be bypassed or inspected directly.
/// </para>
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
    /// Joins the group for one server's interactive live events, after confirming the caller's tenant
    /// can actually see it - reuses <see cref="IRustServerRepository.GetByIdAsync"/>'s existing
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
    /// Joins the group for one server's <em>unfiltered</em> live events - interactive and
    /// non-interactive alike. Gated on <see cref="TokenController.ActingAsClaimType"/> in addition to
    /// the same tenant-visibility check <see cref="JoinServerGroup"/> already applies: only a site
    /// admin currently acting as this server's tenant may join, matching the Panel's own "show
    /// unfiltered" toggle, which is only ever offered to that same caller - see this class's remarks.
    /// </summary>
    public async Task JoinUnfilteredServerGroup(Guid serverId)
    {
        var isActingAsTenant = Context.User?.HasClaim(c => c.Type == TokenController.ActingAsClaimType && c.Value == "true") ?? false;
        if (!isActingAsTenant)
        {
            throw new HubException("Only a site admin acting as this server's tenant may view unfiltered events.");
        }

        var server = await _rustServerRepository.GetByIdAsync(serverId, null);
        if (server is null)
        {
            throw new HubException("Server not found, or it's not one of yours.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UnfilteredGroupName(serverId));
    }

    public async Task LeaveUnfilteredServerGroup(Guid serverId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UnfilteredGroupName(serverId));
    }

    /// <summary>
    /// The SignalR group name for one server's interactive events - shared with the consumers that
    /// push into it. Anyone who can see the server may join this group.
    /// </summary>
    public static string GroupName(Guid serverId) => $"server-{serverId}";

    /// <summary>
    /// The SignalR group name for one server's unfiltered (interactive + non-interactive) events. Only
    /// <see cref="JoinUnfilteredServerGroup"/> ever admits a connection to it.
    /// </summary>
    public static string UnfilteredGroupName(Guid serverId) => $"server-{serverId}-unfiltered";
}
