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
            Stacktrace = message.Stacktrace
        };

        await repository.AddAsync(rconEvent);

        var dto = mapper.Map<RconEventDto>(rconEvent);
        await hubContext.Clients.Group(RconHub.GroupName(message.ServerId)).SendAsync("ReceiveEvent", dto);
    }
}
