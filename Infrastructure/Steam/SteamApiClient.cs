// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Api.Infrastructure.Steam;

/// <summary>
/// <see cref="ISteamApiClient"/> implementation backed by Steam's public Web API
/// (<c>ISteamUser/GetPlayerBans</c> and <c>IPlayerService/GetOwnedGames</c>).
/// </summary>
/// <remarks>
/// Registered as a typed <see cref="HttpClient"/> (see Program.cs). The API key comes in per-call
/// (a server's own configured <see cref="Data.RustServer.SteamApiKey"/>, decrypted by the caller) -
/// this class holds none itself, so the same instance serves every server regardless of which key
/// each uses.
/// </remarks>
public class SteamApiClient(HttpClient httpClient, ILogger<SteamApiClient> logger) : ISteamApiClient
{
    // Rust's own Steam App ID - stable and well-known, not configurable.
    private const int RustAppId = 252490;

    /// <inheritdoc />
    public async Task<SteamPlayerInfo?> GetPlayerInfoAsync(string apiKey, string steamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }

        // Two independent calls, not one dependent on the other - a private profile still returns
        // valid ban data (bans are always public), so one having nothing to say shouldn't take the
        // other down with it.
        var bansTask = GetPlayerBansAsync(apiKey, steamId, cancellationToken);
        var playtimeTask = GetRustPlaytimeMinutesAsync(apiKey, steamId, cancellationToken);
        await Task.WhenAll(bansTask, playtimeTask);

        var bans = bansTask.Result;
        if (bans is null)
        {
            // No ban data means Steam didn't recognize the account at all (bad key or bad SteamId) -
            // treat the whole lookup as a miss rather than reporting a player with unknown-everything.
            return null;
        }

        return new SteamPlayerInfo(bans.VACBanned, bans.NumberOfVACBans, bans.NumberOfGameBans, playtimeTask.Result);
    }

    private async Task<PlayerBansResponseModel.Player?> GetPlayerBansAsync(string apiKey, string steamId, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={Uri.EscapeDataString(apiKey)}&steamids={Uri.EscapeDataString(steamId)}";
            var response = await httpClient.GetFromJsonAsync<PlayerBansResponseModel>(url, cancellationToken);
            return response?.Players is { Count: > 0 } players ? players[0] : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Steam GetPlayerBans lookup failed for {SteamId}", steamId);
            return null;
        }
    }

    private async Task<int?> GetRustPlaytimeMinutesAsync(string apiKey, string steamId, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                $"?key={Uri.EscapeDataString(apiKey)}&steamid={Uri.EscapeDataString(steamId)}" +
                $"&include_played_free_games=1&appids_filter[0]={RustAppId}";
            var response = await httpClient.GetFromJsonAsync<OwnedGamesResponseModel>(url, cancellationToken);
            return response?.Response?.Games?.Find(g => g.AppId == RustAppId)?.PlaytimeForever;
        }
        catch (Exception ex)
        {
            // A private profile isn't an error worth a warning - Steam returns a perfectly valid,
            // empty "response": {} for that case, no games array at all. This only fires for a
            // genuine transport/deserialization failure.
            logger.LogDebug(ex, "Steam GetOwnedGames lookup failed or was unavailable for {SteamId}", steamId);
            return null;
        }
    }

    // Response shapes - only the fields this client actually reads. GetPlayerBans uses PascalCase
    // field names (unusual among Steam Web API endpoints, but that's genuinely how this one responds);
    // GetOwnedGames uses snake_case like most others.
    private sealed class PlayerBansResponseModel
    {
        [JsonPropertyName("players")]
        public List<Player>? Players { get; set; }

        public sealed class Player
        {
            [JsonPropertyName("VACBanned")]
            public bool VACBanned { get; set; }

            [JsonPropertyName("NumberOfVACBans")]
            public int NumberOfVACBans { get; set; }

            [JsonPropertyName("NumberOfGameBans")]
            public int NumberOfGameBans { get; set; }
        }
    }

    private sealed class OwnedGamesResponseModel
    {
        [JsonPropertyName("response")]
        public ResponseBody? Response { get; set; }

        public sealed class ResponseBody
        {
            [JsonPropertyName("games")]
            public List<Game>? Games { get; set; }
        }

        public sealed class Game
        {
            [JsonPropertyName("appid")]
            public int AppId { get; set; }

            [JsonPropertyName("playtime_forever")]
            public int PlaytimeForever { get; set; }
        }
    }
}
