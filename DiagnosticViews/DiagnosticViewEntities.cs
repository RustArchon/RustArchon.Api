// Copyright ©2026 Scott Blomfield

using System;
using RustArchon.Api.Data;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>
/// Every entity a diagnostic view reads from. The backstop test walks this list, so a view that starts reading a new entity must add it here (and mark
/// anything secret on it with <see cref="DiagnosticsHiddenAttribute"/>).
/// </summary>
public static class DiagnosticViewEntities
{
    public static readonly Type[] All =
    [
        typeof(RustServer),
        typeof(ServerPluginStatus),
        typeof(ServerPlugin),
        typeof(PluginUpdateNotice),
        typeof(PluginDownloadLookup),
        typeof(ThirdPartyPluginUpdate),
        typeof(PluginUpdateAttempt),
        typeof(PluginUpdateExclusion),
        typeof(RconEvent),
        typeof(PluginKeyHistory),
        typeof(Plan),
        typeof(PlatformSetting)
    ];
}
