// Copyright ©2026 Scott Blomfield

using System.Linq;
using System.Threading.Tasks;
using MassTransit;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Makes a server's stored <see cref="ServerPlugin"/> rows match the plugin list the Worker just
/// polled, and its <see cref="PluginLoadFailure"/> rows match the plugins Carbon says failed to load. No SignalR relay - nothing on the
/// Plugins tab needs to react to an individual poll landing; it just re-reads the current list when opened or refreshed (see
/// <c>ServerDetail.razor</c>).
/// </summary>
public class ServerPluginsCapturedConsumer(IServerPluginRepository repository, IPluginLoadFailureRepository failureRepository)
    : IConsumer<ServerPluginsCaptured>
{
    public async Task Consume(ConsumeContext<ServerPluginsCaptured> context)
    {
        var message = context.Message;

        var plugins = message.Plugins
            .Select(p => new ServerPlugin
            {
                Name = p.Name,
                Author = p.Author,
                Version = p.Version,
                Framework = message.Framework
            })
            .ToList();

        await repository.ReplaceForServerAsync(message.TenantId, message.ServerId, plugins, message.CapturedAtUtc);

        // Only Carbon reports failed plugins. Anything else (Oxide, no framework) means whatever is stored is out of date, so it is cleared; a
        // Carbon reply that did not carry the section (null) says nothing, so what is stored stays.
        if (message.Framework != ServerModFramework.Carbon)
        {
            await failureRepository.ReplaceForServerAsync(message.TenantId, message.ServerId, [], message.CapturedAtUtc);
        }
        else if (message.Failures is not null)
        {
            var failures = message.Failures
                .Take(MaxFailures)
                .Select(f => new PluginLoadFailure
                {
                    FileName = Limit(f.File, 260),
                    Line = f.Line,
                    Column = f.Column,
                    Message = Limit(f.Message, 500)
                })
                .ToList();
            await failureRepository.ReplaceForServerAsync(message.TenantId, message.ServerId, failures, message.CapturedAtUtc);
        }
    }

    /// <summary>The most reasons kept for one server. The Worker already stops at this many; the Api does not rely on it.</summary>
    public const int MaxFailures = 100;

    // Text from a game server's console: bounded to the column, whatever the sender sent.
    private static string Limit(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max];
}
