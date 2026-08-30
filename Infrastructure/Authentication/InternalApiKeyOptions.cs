// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.Authentication;

/// <summary>
/// Backs the <c>RUSTARCHON_INTERNAL_API_KEY</c> config key - the shared secret that authenticates
/// service-to-service calls to <see cref="Controllers.InternalController"/>, distinct from both the
/// end-user JWT scheme and the config-driven <c>PlatformAdmin</c> authorization policy set up in
/// <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Bound via a manual <c>Configure&lt;InternalApiKeyOptions&gt;</c> delegate in <c>Program.cs</c>
/// (not a section) reading that one flat key directly. Callers are RustArchon's own other processes,
/// not end users or admins - today that's the Blazor web app's <c>QueuedEmailSender</c> and
/// <c>RustArchon.Worker</c>'s <c>IInternalApiClient</c>, both of which read the exact same
/// <c>RUSTARCHON_INTERNAL_API_KEY</c> name with no intermediate rename (see their own
/// <c>Program.cs</c> files) - one env var, one name, everywhere it's needed.
/// </para>
/// <para>
/// See <see cref="InternalApiKeyAuthenticationHandler"/> for how this is actually checked.
/// </para>
/// </remarks>
public class InternalApiKeyOptions
{
    /// <summary>
    /// Gets or sets the shared secret expected in the <c>X-Internal-Api-Key</c> header.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;
}
