// Copyright ©2026 Scott Blomfield

using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Infrastructure;

/// <summary>The public half of a deployment's plugin signing key, as the plugin embeds it.</summary>
public sealed record PluginPublicKey(string ModulusBase64, string ExponentBase64, string Fingerprint);

/// <summary>A key that cannot sign: revoked, or not one this Panel has ever had.</summary>
/// <param name="State"><c>null</c> for a fingerprint this Panel has never had.</param>
public sealed class PluginKeyUnavailableException(string fingerprint, PluginKeyState? state)
    : Exception($"The plugin signing key {fingerprint} is {(state is null ? "unknown to this Panel" : state.ToString()!.ToLowerInvariant())} and cannot sign.")
{
    public string Fingerprint { get; } = fingerprint;
    public PluginKeyState? State { get; } = state;
}

/// <summary>The stored signing key could not be used. Never resolved by generating a new one - see the service.</summary>
public sealed class PluginSigningKeyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// This deployment's RustArchon-plugin signing key: created on first use, held encrypted in Platform Settings, and
/// used to sign the script the Panel serves. RSA-2048, SHA-256, PKCS#1 v1.5 - the only scheme the game server's
/// Mono runtime can verify.
/// </summary>
public interface IPluginSigningService
{
    Task<PluginPublicKey> GetPublicKeyAsync();

    /// <summary>
    /// This deployment's key fingerprint, or <c>null</c> if it has none yet or the stored key cannot be read.
    /// <b>Never creates a key</b> - unlike <see cref="GetPublicKeyAsync"/> - so it is safe on a plain read (showing
    /// whether an installed plugin trusts this Panel) without the side effect of minting a key nobody asked for.
    /// </summary>
    Task<string?> TryGetFingerprintAsync();

    /// <summary>Signs <paramref name="payload"/> with the deployment key (creating the key first if there is none).</summary>
    Task<byte[]> SignAsync(byte[] payload);

    /// <summary>
    /// Where a key stands: <see cref="PluginKeyState.Active"/>, <see cref="PluginKeyState.Retired"/>,
    /// <see cref="PluginKeyState.Revoked"/>, or <c>null</c> for a fingerprint this Panel has never had. Never creates a
    /// key, and never throws for one that cannot be read - "unknown" is the fail-closed answer.
    /// </summary>
    Task<PluginKeyState?> GetKeyStateAsync(string fingerprint);

    /// <summary>
    /// Signs with the key that has this fingerprint: the active key, or a retired one (to bridge a server still on it
    /// to the active key).
    /// </summary>
    /// <exception cref="PluginKeyUnavailableException">The key is revoked or unknown.</exception>
    Task<byte[]> SignWithAsync(string fingerprint, byte[] payload);
}

/// <inheritdoc cref="IPluginSigningService" />
/// <remarks>
/// <para>
/// <b>Fails closed, never regenerates.</b> If a key is stored but cannot be decrypted or parsed (someone pasted junk
/// over it in the Platform Settings page, or the Data Protection key ring was lost), this throws
/// <see cref="PluginSigningKeyException"/> rather than quietly making a new key: a new key would strand every plugin
/// already installed from this Panel, and would do it silently.
/// </para>
/// <para>
/// First-time creation is race safe: the new key is written with an atomic set-if-empty, and whichever caller loses
/// the race discards its own key and uses the stored one, so two instances handling the first download together can
/// never sign with different keys.
/// </para>
/// </remarks>
public class PluginSigningService(
    IPlatformSettingRepository settings,
    IApiKeyProtector protector,
    ILogger<PluginSigningService> logger,
    IPluginKeyHistoryRepository? history = null) : IPluginSigningService
{
    private const int MinimumKeySizeBits = 2048;

    public async Task<PluginPublicKey> GetPublicKeyAsync()
    {
        using var rsa = await LoadKeyAsync();
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var modulus = Convert.ToBase64String(parameters.Modulus!);
        return new PluginPublicKey(modulus, Convert.ToBase64String(parameters.Exponent!), PluginScriptStamper.Fingerprint(modulus));
    }

    public async Task<string?> TryGetFingerprintAsync()
    {
        var setting = await settings.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey);
        if (string.IsNullOrEmpty(setting?.Value))
        {
            return null; // no key yet - and this read must not be the thing that makes one
        }

        try
        {
            return (await GetPublicKeyAsync()).Fingerprint;
        }
        catch (PluginSigningKeyException)
        {
            return null;
        }
    }

    public async Task<byte[]> SignAsync(byte[] payload)
    {
        using var rsa = await LoadKeyAsync();
        return rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public async Task<PluginKeyState?> GetKeyStateAsync(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return null;
        }

        var active = await TryGetFingerprintAsync();
        if (active is not null && string.Equals(active, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return PluginKeyState.Active;
        }

        var old = history is null ? null : await history.GetByFingerprintAsync(fingerprint.ToLowerInvariant());
        return old?.State;
    }

    public async Task<byte[]> SignWithAsync(string fingerprint, byte[] payload)
    {
        var state = await GetKeyStateAsync(fingerprint);
        switch (state)
        {
            case PluginKeyState.Active:
                return await SignAsync(payload);

            case PluginKeyState.Retired:
                var old = await history!.GetByFingerprintAsync(fingerprint.ToLowerInvariant());
                using (var rsa = ImportKey(old!.EncryptedPrivateKey))
                {
                    return rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }

            default:
                throw new PluginKeyUnavailableException(fingerprint, state);
        }
    }

    private async Task<RSA> LoadKeyAsync()
    {
        var setting = await settings.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey)
            ?? throw new PluginSigningKeyException(
                $"The '{PlatformSettingsRegistry.PluginSigningKey}' platform setting does not exist; the settings registry has not run.");

        if (string.IsNullOrEmpty(setting.Value))
        {
            using var generated = RSA.Create(MinimumKeySizeBits);
            var stored = protector.Protect(
                ApiKeyProtectorPurposes.PluginSigningKey, Convert.ToBase64String(generated.ExportPkcs8PrivateKey()));

            if (await settings.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, stored))
            {
                logger.LogInformation("Generated this deployment's RustArchon plugin signing key.");
            }

            // Win or lose the race, use what is stored, so every caller signs with the one key.
            setting = await settings.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey);
            if (string.IsNullOrEmpty(setting?.Value))
            {
                throw new PluginSigningKeyException("The plugin signing key could not be stored.");
            }
        }

        return ImportKey(setting!.Value);
    }

    /// <summary>Decrypts and loads a stored private key, refusing anything unreadable or under 2048 bits.</summary>
    private RSA ImportKey(string stored)
    {
        RSA? rsa = null;
        try
        {
            rsa = RSA.Create();
            var pkcs8 = Convert.FromBase64String(protector.Unprotect(ApiKeyProtectorPurposes.PluginSigningKey, stored));
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);

            if (rsa.KeySize < MinimumKeySizeBits)
            {
                throw new PluginSigningKeyException($"The stored plugin signing key is {rsa.KeySize} bits; at least {MinimumKeySizeBits} is required.");
            }

            return rsa;
        }
        catch (PluginSigningKeyException)
        {
            rsa?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            rsa?.Dispose();
            throw new PluginSigningKeyException(
                "The stored plugin signing key cannot be read. It was not replaced, because a new key would strand every " +
                "plugin already installed from this Panel.", ex);
        }
    }
}

/// <summary>Where the plugin sources this Panel stamps, signs and serves come from.</summary>
public interface IPluginScriptSource
{
    /// <summary>The main plugin (<c>RustArchon.cs</c>) this Panel currently serves.</summary>
    Task<string> ReadSourceAsync();

    /// <summary>The Updater plugin (<c>RustArchonUpdater.cs</c>) this Panel currently serves.</summary>
    Task<string> ReadUpdaterSourceAsync();
}

/// <summary>Reads the plugin sources embedded into this assembly from the sibling <c>RustArchon.Plugin</c> repo.</summary>
public class EmbeddedPluginScriptSource : IPluginScriptSource
{
    public const string ResourceName = "RustArchon.Plugin.RustArchon.cs";
    public const string UpdaterResourceName = "RustArchon.Plugin.RustArchonUpdater.cs";

    public string ReadSource() => Read(ResourceName, "RustArchon.Plugin/src/RustArchon.cs");

    public string ReadUpdaterSource() => Read(UpdaterResourceName, "RustArchon.Plugin/src/RustArchonUpdater.cs");

    public Task<string> ReadSourceAsync() => Task.FromResult(ReadSource());

    public Task<string> ReadUpdaterSourceAsync() => Task.FromResult(ReadUpdaterSource());

    private static string Read(string resourceName, string sourcePath)
    {
        using var stream = typeof(EmbeddedPluginScriptSource).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The plugin source is not embedded in this build (no resource '{resourceName}'); the Api project must include {sourcePath}.");
        using var reader = new System.IO.StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>A finished, signed plugin script ready to serve.</summary>
public sealed record PluginScript(byte[] Bytes, string KeyFingerprint, string? PluginVersion);

public interface IPluginScriptService
{
    /// <summary>The main plugin, stamped and signed.</summary>
    Task<PluginScript> BuildAsync();

    /// <summary>The Updater plugin, stamped and signed the same way and with the same key.</summary>
    Task<PluginScript> BuildUpdaterAsync();

    /// <summary>
    /// The main plugin for a server whose installed plugin trusts the key with this fingerprint. If that is the active
    /// key this is exactly <see cref="BuildAsync"/>. If it is a retired one, the file is a <b>bridge</b>: it embeds the
    /// active key and is signed twice - by the active key over the source (so the new plugin can vouch for itself),
    /// then by the retired key over all of that (the last line, which the server's Updater checks with the key it trusts).
    /// One hop, however many rotations behind the server is.
    /// </summary>
    /// <exception cref="PluginKeyUnavailableException">That key is revoked, or was never this Panel's.</exception>
    Task<PluginScript> BuildBridgeAsync(string trustedKeyFingerprint);

    /// <summary>Where a key stands - see <see cref="IPluginSigningService.GetKeyStateAsync"/>.</summary>
    Task<PluginKeyState?> GetKeyStateAsync(string fingerprint);

    /// <summary>
    /// The version of the main plugin this Panel currently serves - what an installed plugin would be updated
    /// <em>to</em>. Read from the embedded source; needs no key, so it never creates one.
    /// </summary>
    Task<string?> GetLatestVersionAsync();

    /// <summary>The Updater version this Panel serves.</summary>
    Task<string?> GetLatestUpdaterVersionAsync();

    /// <summary>This deployment's key fingerprint if it already has a key, else <c>null</c>. Never creates one.</summary>
    Task<string?> GetKeyFingerprintIfAnyAsync();
}

/// <summary>Stamps this deployment's public key into a plugin source and signs the result.</summary>
public partial class PluginScriptService(IPluginSigningService signing, IPluginScriptSource source) : IPluginScriptService
{
    public async Task<PluginScript> BuildAsync() => await BuildAsync(await source.ReadSourceAsync(), "RustArchon");

    public async Task<PluginScript> BuildUpdaterAsync() => await BuildAsync(await source.ReadUpdaterSourceAsync(), "RustArchonUpdater");

    public Task<PluginKeyState?> GetKeyStateAsync(string fingerprint) => signing.GetKeyStateAsync(fingerprint);

    public async Task<PluginScript> BuildBridgeAsync(string trustedKeyFingerprint)
    {
        var state = await signing.GetKeyStateAsync(trustedKeyFingerprint);
        if (state is null or PluginKeyState.Revoked)
        {
            throw new PluginKeyUnavailableException(trustedKeyFingerprint, state);
        }

        if (state == PluginKeyState.Active)
        {
            return await BuildAsync();
        }

        var text = await source.ReadSourceAsync();
        var active = await signing.GetPublicKeyAsync();

        // The active key vouches for the source first (this is what the new plugin checks about itself)...
        var payload = PluginScriptStamper.Stamp(text, active.ModulusBase64, active.ExponentBase64);
        var cosigned = PluginScriptStamper.Attach(payload, await signing.SignAsync(payload));

        // ...then the key the server trusts today signs everything, cosignature line included: the last line.
        var bridged = PluginScriptStamper.Attach(cosigned, await signing.SignWithAsync(trustedKeyFingerprint, cosigned));

        return new PluginScript(bridged, active.Fingerprint, ReadVersion(text, "RustArchon"));
    }

    public async Task<string?> GetLatestVersionAsync() => ReadVersion(await source.ReadSourceAsync(), "RustArchon");

    public async Task<string?> GetLatestUpdaterVersionAsync() => ReadVersion(await source.ReadUpdaterSourceAsync(), "RustArchonUpdater");

    public Task<string?> GetKeyFingerprintIfAnyAsync() => signing.TryGetFingerprintAsync();

    private async Task<PluginScript> BuildAsync(string text, string pluginTitle)
    {
        var key = await signing.GetPublicKeyAsync();

        var payload = PluginScriptStamper.Stamp(text, key.ModulusBase64, key.ExponentBase64);
        var signature = await signing.SignAsync(payload);

        return new PluginScript(PluginScriptStamper.Attach(payload, signature), key.Fingerprint, ReadVersion(text, pluginTitle));
    }

    // The [Info("<title>", "<author>", "x.y.z")] attribute's third argument.
    private static string? ReadVersion(string text, string title)
    {
        var match = Regex.Match(
            text, @"\[Info\(""" + Regex.Escape(title) + @"""\s*,\s*""[^""]*""\s*,\s*""([^""]+)""\)\]");
        return match.Success ? match.Groups[1].Value : null;
    }
}
