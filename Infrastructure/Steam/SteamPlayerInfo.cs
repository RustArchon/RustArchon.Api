// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.Steam;

/// <summary>
/// What <see cref="ISteamApiClient"/> reports about a Steam account.
/// </summary>
/// <param name="VacBanned">Whether the account currently has an active VAC ban on record.</param>
/// <param name="NumberOfVacBans">Total VAC bans across every game, not just Rust.</param>
/// <param name="NumberOfGameBans">Total game (community/publisher) bans across every game.</param>
/// <param name="MinutesPlayedForever">
/// Total lifetime playtime in Rust specifically, in minutes, per Steam's own records. <c>null</c> if
/// the account's game details aren't public (Steam simply omits Rust from the response rather than
/// erroring), or the account doesn't own/hasn't played Rust at all - either way, not an error.
/// </param>
public record SteamPlayerInfo(bool VacBanned, int NumberOfVacBans, int NumberOfGameBans, int? MinutesPlayedForever);
