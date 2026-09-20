// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Infrastructure;

/// <summary>One <c>archon.config set</c> command the plugin needs to be sent, and what it changes.</summary>
public sealed record PluginSettingCommand(string Setting, bool Value)
{
    public string Command => $"archon.config set {Setting} {(Value ? "true" : "false")}";
}

/// <summary>
/// Decides which <c>archon.config</c> commands bring a plugin's reported switches in line with a server's saved
/// (desired) ones. Pure, so the rule is testable without a bus or a database.
/// </summary>
public static class PluginSettingsReconciler
{
    public const string ConfigCapability = RustArchonPlugin.ConfigCapability;
    public const string RecordingSetting = "recording";
    public const string CombatSetting = "combat";

    /// <summary>
    /// The commands needed, in a stable order. Empty when nothing differs, and <b>also empty when the plugin did
    /// not report the <c>config</c> capability</b>: fail closed, never send a command a build has not said it
    /// understands.
    /// </summary>
    public static IReadOnlyList<PluginSettingCommand> CommandsFor(
        bool desiredRecording,
        bool desiredCombat,
        bool reportedRecording,
        bool reportedCombat,
        IReadOnlyCollection<string> capabilities)
    {
        if (!capabilities.Contains(ConfigCapability, StringComparer.Ordinal))
        {
            return [];
        }

        var commands = new List<PluginSettingCommand>();
        if (desiredRecording != reportedRecording)
        {
            commands.Add(new PluginSettingCommand(RecordingSetting, desiredRecording));
        }
        if (desiredCombat != reportedCombat)
        {
            commands.Add(new PluginSettingCommand(CombatSetting, desiredCombat));
        }
        return commands;
    }
}

/// <summary>Brings one server's RustArchon plugin in line with the server's saved switch settings.</summary>
public interface IPluginSettingsSynchronizer
{
    Task SyncAsync(Guid tenantId, Guid serverId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends the commands <see cref="PluginSettingsReconciler"/> asks for, through the Worker that owns the server's
/// RCON connection (<see cref="SendRconCommand"/>), non-interactively so they never appear as something a person
/// typed in the Console tab.
/// </summary>
/// <remarks>
/// Best effort by design: if the server is not connected or does not answer in time, this stops quietly. The
/// next handshake (every few minutes) finds the same difference and tries again, so there is no retry state to
/// keep here. It does nothing at all until the plugin has said hello at least once.
/// </remarks>
public class PluginSettingsSynchronizer(
    IRustServerRepository serverRepository,
    IServerPluginStatusRepository statusRepository,
    IRequestClient<SendRconCommand> sendCommandClient,
    ILogger<PluginSettingsSynchronizer> logger) : IPluginSettingsSynchronizer
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public async Task SyncAsync(Guid tenantId, Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await serverRepository.GetByIdAcrossTenantsAsync(serverId);
        if (server is null || server.TenantId != tenantId || !server.IsEnabled)
        {
            return;
        }

        var status = await statusRepository.GetForServerAcrossTenantsAsync(tenantId, serverId);
        if (status is null)
        {
            return;
        }

        var commands = PluginSettingsReconciler.CommandsFor(
            server.PluginRecordingEnabled,
            server.PluginCombatLogEnabled,
            status.ReportedRecordingEnabled,
            status.ReportedCombatLogEnabled,
            status.Capabilities);
        if (commands.Count == 0)
        {
            return;
        }

        var recording = status.ReportedRecordingEnabled;
        var combat = status.ReportedCombatLogEnabled;
        var anyApplied = false;

        foreach (var command in commands)
        {
            try
            {
                var response = await sendCommandClient.GetResponse<RconCommandResult>(
                    new SendRconCommand(serverId, command.Command, Interactive: false),
                    cancellationToken,
                    RequestTimeout.After(s: 10));

                // The plugin answers with an envelope; only {"ok":true} means the change was made.
                if (!response.Message.Success || response.Message.Message?.Contains("\"ok\":true", StringComparison.Ordinal) != true)
                {
                    logger.LogWarning(
                        "RustArchon plugin on server {ServerId} did not accept '{Command}': {Detail}",
                        serverId, command.Command, response.Message.Error ?? response.Message.Message);
                    break;
                }

                if (command.Setting == PluginSettingsReconciler.RecordingSetting) { recording = command.Value; }
                if (command.Setting == PluginSettingsReconciler.CombatSetting) { combat = command.Value; }
                anyApplied = true;
            }
            catch (RequestTimeoutException)
            {
                logger.LogInformation("Timed out sending '{Command}' to server {ServerId}; the next handshake will retry.", command.Command, serverId);
                break;
            }
        }

        if (anyApplied)
        {
            await statusRepository.MarkSettingsAppliedAsync(tenantId, serverId, recording, combat);
        }
    }
}
