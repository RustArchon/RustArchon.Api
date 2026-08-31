// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;

namespace RustArchon.Api.Infrastructure.Steam;

/// <summary>
/// Looks up a Steam account's ban status and Rust-specific playtime via Steam's Web API.
/// </summary>
public interface ISteamApiClient
{
    /// <summary>
    /// Returns <c>null</c> (never throws for an ordinary "no answer" case) if <paramref name="apiKey"/>
    /// is invalid/rate-limited or <paramref name="steamId"/> doesn't resolve to a real account. A
    /// non-null result's <see cref="SteamPlayerInfo.MinutesPlayedForever"/> can still independently be
    /// <c>null</c> - see its own remarks.
    /// </summary>
    Task<SteamPlayerInfo?> GetPlayerInfoAsync(string apiKey, string steamId, CancellationToken cancellationToken);
}
