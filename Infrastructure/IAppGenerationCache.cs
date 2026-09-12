// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// A single shared "has something every open Panel circuit is rendering just changed" signal - not a
/// cache of any particular value, just a marker an already-open circuit can compare itself against to
/// tell whether its next navigation needs to be a full page reload instead of an in-place one.
/// </summary>
/// <remarks>
/// <para>
/// Solves a different problem than <see cref="IPlatformSettingsCache"/>: that one makes sure every
/// *new* request sees the latest value (a shared Valkey key everyone reads straight through - see its
/// own remarks on why no invalidation message is needed for that). This one is for circuits that are
/// already open and rendered - a Blazor Server circuit renders its <c>&lt;head&gt;</c> once, at the
/// first page load, and never re-runs it just because a setting changed server-side underneath it. A
/// site name change lands in <see cref="IPlatformSettingsCache"/> immediately for anyone loading a page
/// from now on, but someone already mid-session, clicking around, would otherwise keep seeing the old
/// name in the nav bar until they happened to hit refresh.
/// </para>
/// <para>
/// Deliberately not Postgres-backed the way <see cref="Data.PlatformSetting"/> is - there is nothing
/// here worth persisting durably. If Valkey restarts and this value is lost, the worst that happens is
/// every open circuit's next navigation gets upgraded to a full reload once, unnecessarily; that is a
/// harmless, self-correcting cost, not a data-loss concern. See RustArchon.Panel's own read side (which
/// reads this key through the same <c>IValkeyCache</c> everything else there uses) for how "Valkey
/// unreachable" degrades to "can't tell, so don't force a reload" rather than an error.
/// </para>
/// </remarks>
public interface IAppGenerationCache
{
    /// <summary>
    /// Records that something every open circuit renders has changed - RustArchon.Panel's own
    /// navigation check picks this up the next time any circuit attempts to navigate anywhere,
    /// wherever that navigation starts. Call this alongside (not instead of) whatever normally
    /// persists/caches the actual change - see <c>PlatformSettingsController.UpdateValue</c> for the
    /// first caller.
    /// </summary>
    Task BumpAsync();
}
