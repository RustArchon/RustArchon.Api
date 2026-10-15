// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Keeps the log entries a Worker forwarded in memory (<see cref="WorkerLogStore"/>) so the diagnostic log endpoint can serve them.
/// Nothing is persisted and nothing is retried: see <see cref="WorkerLogBatch"/>'s remarks.
/// </summary>
public class WorkerLogBatchConsumer(WorkerLogStore store) : IConsumer<WorkerLogBatch>
{
    public Task Consume(ConsumeContext<WorkerLogBatch> context)
    {
        store.Ingest(context.Message);
        return Task.CompletedTask;
    }
}
