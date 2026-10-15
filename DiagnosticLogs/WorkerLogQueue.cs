// Copyright ©2026 Scott Blomfield

using MassTransit;
using RustArchon.Api.Messaging;

namespace RustArchon.Api.DiagnosticLogs;

/// <summary>
/// The queue the Workers' <c>WorkerLogBatch</c> messages arrive on. Batches are worth only as much as they are fresh, so the queue expires messages
/// after two minutes and holds at most a thousand: an Api that was down does not wake up to a flood of stale batches.
/// </summary>
/// <remarks>
/// It is durable on purpose. The first version was non-durable and non-exclusive, which RabbitMQ 4 refuses outright (INTERNAL_ERROR, the deprecated
/// feature <c>transient_nonexcl_queues</c>): it worked on an older broker and failed on production's, so no batch ever arrived. The expiry and the length
/// cap, not transience, are what keep it small. The name changed with the declaration so a broker that already holds the old non-durable queue under the
/// old name cannot refuse the new one for disagreeing with it.
/// </remarks>
public static class WorkerLogQueue
{
    public const string Name = "rustarchon-api-worker-log-batches";

    public static void Configure(IRabbitMqReceiveEndpointConfigurator endpoint, IRegistrationContext context)
    {
        endpoint.Durable = true;
        endpoint.SetQueueArgument("x-message-ttl", 120_000);
        endpoint.SetQueueArgument("x-max-length", 1_000);
        endpoint.ConfigureConsumer<WorkerLogBatchConsumer>(context);
    }
}
