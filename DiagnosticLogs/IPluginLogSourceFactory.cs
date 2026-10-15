// Copyright ©2026 Scott Blomfield

using MassTransit;
using RustArchon.Api.Repositories;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.DiagnosticLogs;

/// <summary>Makes the log source for one game server's RustArchon plugin, read on demand through the Worker's RCON connection.</summary>
public interface IPluginLogSourceFactory
{
    ILogSource Create(Guid serverId);
}

/// <inheritdoc />
public sealed class PluginLogSourceFactory(
    IRustServerRepository servers, IServerPluginStatusRepository statuses, IRequestClient<SendRconCommand> rcon) : IPluginLogSourceFactory
{
    public ILogSource Create(Guid serverId) => new PluginLogSource(serverId, servers, statuses, rcon);
}
