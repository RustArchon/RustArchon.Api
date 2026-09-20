// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;


namespace RustArchon.Api.Messaging;

/// <summary>
/// Stores what the RustArchon plugin reported in its <c>archon.hello</c> handshake, then brings the plugin's
/// switches in line with the server's saved settings if they differ (a plugin reinstall or a reset settings file
/// looks exactly like that). No SignalR relay: nothing in the Panel reacts to an individual handshake landing.
/// </summary>
public class ServerPluginHandshakeCapturedConsumer(
    IServerPluginStatusRepository statusRepository,
    IPluginSettingsSynchronizer synchronizer) : IConsumer<ServerPluginHandshakeCaptured>
{
    public async Task Consume(ConsumeContext<ServerPluginHandshakeCaptured> context)
    {
        var message = context.Message;

        await statusRepository.UpsertAsync(message.TenantId, message.ServerId, new ServerPluginStatus
        {
            ProtocolVersion = message.ProtocolVersion,
            PluginVersion = message.PluginVersion,
            Capabilities = [.. message.Capabilities],
            ReportedRecordingEnabled = message.RecordingEnabled,
            ReportedCombatLogEnabled = message.CombatLogEnabled,
            SettingsPersisted = message.SettingsPersisted,
            // Normalized again here, not just in the Worker: these are stored and shown, they come from a plugin on
            // someone else's server, and a message from an older Worker build carries neither (read as null).
            SigningState = PluginSigningStates.Normalize(message.SigningState),
            SigningKeyFingerprint = PluginSigningStates.NormalizeFingerprint(message.SigningKeyFingerprint),
            CapturedAtUtc = message.CapturedAtUtc
        });

        await synchronizer.SyncAsync(message.TenantId, message.ServerId, context.CancellationToken);
    }
}

/// <summary>
/// Applies a server's just-changed switch settings to its plugin immediately - see
/// <see cref="ServerPluginSettingsChanged"/>.
/// </summary>
public class ServerPluginSettingsChangedConsumer(IPluginSettingsSynchronizer synchronizer)
    : IConsumer<ServerPluginSettingsChanged>
{
    public Task Consume(ConsumeContext<ServerPluginSettingsChanged> context) =>
        synchronizer.SyncAsync(context.Message.TenantId, context.Message.ServerId, context.CancellationToken);
}
