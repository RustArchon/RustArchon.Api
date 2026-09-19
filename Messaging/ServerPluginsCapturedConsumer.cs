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
/// polled. No SignalR relay - nothing on the Plugins tab needs to react to an individual poll landing;
/// it just re-reads the current list when opened or refreshed (see <c>ServerDetail.razor</c>).
/// </summary>
public class ServerPluginsCapturedConsumer(IServerPluginRepository repository)
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
    }
}
