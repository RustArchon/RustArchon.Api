// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>Writes a line to the plugin audit log (<see cref="PluginAdminEvent"/>) for something that is not part of a larger change.</summary>
/// <remarks>
/// Rotating, revoking, uploading, publishing and withdrawing write their own line in the same transaction as the change itself. This is for the
/// rest: signing a file, and the first key being made. Append-only, like everything in that log.
/// </remarks>
public interface IPluginAdminAudit
{
    Task RecordAsync(PluginAdminEventKind kind, string subject, string actor, string? detail);
}

/// <inheritdoc cref="IPluginAdminAudit" />
public sealed class PluginAdminAudit(ApiDbContext context, TimeProvider clock) : IPluginAdminAudit
{
    public async Task RecordAsync(PluginAdminEventKind kind, string subject, string actor, string? detail)
    {
        context.PluginAdminEvents.Add(new PluginAdminEvent
        {
            AtUtc = clock.GetUtcNow(),
            Kind = kind,
            Subject = Limit(subject, 100) ?? string.Empty,
            Actor = Limit(actor, 200) ?? "unknown",
            Detail = Limit(detail, 1000)
        });
        await context.SaveChangesAsync();
    }

    private static string? Limit(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}
