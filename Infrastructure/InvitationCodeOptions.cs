// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Backs the <c>RUSTARCHON_INVITATION_CODES_ENABLED</c> config key - the kill switch for the whole
/// invitation-gated sign-up feature.
/// </summary>
/// <remarks>
/// <para>
/// Bound via a manual <c>Configure&lt;InvitationCodeOptions&gt;</c> delegate in <c>Program.cs</c>
/// (not a section) reading that one flat key directly, rather than a nested config section - the
/// same env var name reaches this property with no intermediate rename, in <c>.env</c>,
/// <c>docker-compose.yml</c>, and here.
/// </para>
/// <para>
/// To open sign-ups back up after the soft launch, set <c>RUSTARCHON_INVITATION_CODES_ENABLED</c> to
/// <c>false</c> (config only - no code change, no migration) and restart the API. See
/// <see cref="Controllers.InvitationsController"/>, the only place this is read.
/// </para>
/// </remarks>
public class InvitationCodeOptions
{
    /// <summary>
    /// Gets or sets whether registration requires a valid invitation code. Defaults to <c>true</c>
    /// so a missing config value fails closed rather than silently opening sign-ups.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
