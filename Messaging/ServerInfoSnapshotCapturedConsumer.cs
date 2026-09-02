// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Messaging;

/// <summary>
/// Persists a periodic <c>serverinfo</c> poll snapshot for the Stats tab's graphs. No SignalR relay -
/// unlike a connect/disconnect, nothing on the page needs to react to an individual snapshot landing;
/// the Stats tab just re-fetches history on its own schedule (see <c>ServerDetail.razor</c>).
/// </summary>
public class ServerInfoSnapshotCapturedConsumer(IServerInfoSnapshotRepository repository)
    : IConsumer<ServerInfoSnapshotCaptured>
{
    public async Task Consume(ConsumeContext<ServerInfoSnapshotCaptured> context)
    {
        var message = context.Message;

        await repository.AddAsync(new ServerInfoSnapshot
        {
            TenantId = message.TenantId,
            RustServerId = message.ServerId,
            Players = message.Players,
            MaxPlayers = message.MaxPlayers,
            NetworkIn = message.NetworkIn,
            NetworkOut = message.NetworkOut,
            Memory = message.Memory,
            CapturedAtUtc = message.CapturedAtUtc
        });
    }
}
