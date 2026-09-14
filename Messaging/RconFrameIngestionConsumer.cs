// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using AutoMapper;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Persists every captured WebRCON frame as an <see cref="RconEvent"/> and relays it live to any
/// Blazor client currently watching that server's console.
/// </summary>
/// <remarks>
/// Every frame is persisted unconditionally, interactive or not - see <c>RconFrameCaptured</c>'s
/// remarks. The live relay is where <see cref="RconFrameCaptured.Interactive"/> actually matters: an
/// interactive frame reaches <see cref="RconHub.GroupName"/> (every viewer of this server), while a
/// non-interactive one <em>only</em> reaches <see cref="RconHub.UnfilteredGroupName"/> - the group
/// <c>RconHub</c> only lets a site admin acting as this tenant join in the first place. This is the
/// filtering the Panel's own client-side toggle used to attempt: pushing every event to one shared
/// group and having Blazor decide what to render would still transmit privileged/background data to
/// every connection regardless of what got drawn on screen. Routing to a different group entirely
/// means a non-interactive row is never sent over an unauthorized connection at all.
/// </remarks>
public class RconFrameIngestionConsumer(
    IRconEventRepository repository,
    IMapper mapper,
    IHubContext<RconHub> hubContext) : IConsumer<RconFrameCaptured>
{
    public async Task Consume(ConsumeContext<RconFrameCaptured> context)
    {
        var message = context.Message;

        // TenantId comes from the message, not the repository's usual ambient-tenant-context path -
        // there is no HttpContext in a consumer for JwtTenantContext to read a claim from. See
        // Repository<TEntity>.AddAsync's remarks: it only sets TenantId when the ambient context
        // actually resolves one, so setting it explicitly here first is safe - it won't be overwritten.
        var rconEvent = new RconEvent
        {
            Id = Guid.NewGuid(),
            TenantId = message.TenantId,
            RustServerId = message.ServerId,
            CapturedAtUtc = message.CapturedAtUtc,
            Identifier = message.Identifier,
            Type = message.Type,
            Message = message.Message,
            Stacktrace = message.Stacktrace,
            Interactive = message.Interactive,
            Direction = message.Direction
        };

        await repository.AddAsync(rconEvent);

        var dto = mapper.Map<RconEventDto>(rconEvent);

        // Unfiltered viewers (site admins who opted in) see literally everything, interactive or not -
        // sent to this group first and unconditionally so a background row is never gated behind the
        // interactive check below reaching it late. Ordinary viewers only ever get the interactive
        // group, and only interactive rows are ever sent there - see this class's own remarks.
        await hubContext.Clients.Group(RconHub.UnfilteredGroupName(message.ServerId)).SendAsync("ReceiveEvent", dto);

        if (message.Interactive)
        {
            await hubContext.Clients.Group(RconHub.GroupName(message.ServerId)).SendAsync("ReceiveEvent", dto);
        }
    }
}
