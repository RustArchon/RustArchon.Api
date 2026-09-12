// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Consumes <see cref="CommunicationDelivered"/> - RustArchon.Worker's report of what happened when it
/// attempted one <see cref="EmailRequested"/> send - and moves the matching <c>Communication</c> row
/// from <see cref="CommunicationStatus.Queued"/> to <see cref="CommunicationStatus.Sent"/> or
/// <see cref="CommunicationStatus.Bounced"/>.
/// </summary>
/// <remarks>
/// Silently does nothing for an id that no longer exists or isn't still Queued (already cancelled by
/// an admin, or - in principle, though nothing currently causes it - a duplicate delivery report). A
/// message for an unknown or already-resolved communication isn't this consumer's problem to raise;
/// there's nothing actionable an admin could do with a warning about it.
/// </remarks>
public class CommunicationDeliveredConsumer(
    ICommunicationRepository communications, ILogger<CommunicationDeliveredConsumer> logger)
    : IConsumer<CommunicationDelivered>
{
    public async Task Consume(ConsumeContext<CommunicationDelivered> context)
    {
        var message = context.Message;

        var communication = await communications.GetByIdAcrossTenantsAsync(
            message.CommunicationId, context.CancellationToken);

        if (communication is not { Status: CommunicationStatus.Queued })
        {
            logger.LogInformation(
                "Ignoring delivery report for communication {CommunicationId} - not found or no longer Queued.",
                message.CommunicationId);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (message.Success)
        {
            communication.Status = CommunicationStatus.Sent;
            communication.SentOn = now;
        }
        else
        {
            communication.Status = CommunicationStatus.Bounced;
            communication.BouncedOn = now;
            communication.FailureReason = message.Error;
        }

        await communications.SaveAsync(communication, context.CancellationToken);
    }
}
