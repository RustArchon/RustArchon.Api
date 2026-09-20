// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>One signing key as it travels in a bundle: the private key itself (PKCS#8, base64) and where it stands.</summary>
/// <param name="Fingerprint">First 16 hex characters of SHA-256 over the public modulus.</param>
/// <param name="State">Active, Retired or Revoked. At most one key in a bundle is Active.</param>
public sealed record PluginKeyBundleKey(
    string Fingerprint,
    string Pkcs8Base64,
    PluginKeyState State,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedReason);

/// <summary>
/// The file a Panel's signing keys are exported to, so they can be backed up or carried to another Panel: a small JSON
/// container whose secret part (every private key, and where each stands) is encrypted with a key derived from a passphrase.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a bundle at all.</strong> A Panel keeps its keys encrypted under its own data-protection key ring, which is
/// specific to that deployment and cannot be carried anywhere. Backing keys up, or having two Panels sign with the same key,
/// needs them out of that protection and into one that travels: a passphrase.
/// </para>
/// <para>
/// <strong>Format.</strong> PBKDF2-HMAC-SHA256 (600,000 iterations, a random salt) derives a 256-bit key; AES-256-GCM
/// encrypts the payload. The header (format, version, time, KDF parameters, salt, and the list of fingerprints) is bound to
/// the ciphertext as additional authenticated data, so changing any of it makes the file unreadable rather than quietly
/// different. The fingerprints are visible without the passphrase on purpose: they identify which keys a file holds and are
/// not secret (they are what a plugin reports).
/// </para>
/// <para>
/// <strong>What is trusted on the way in.</strong> Nothing in the file is taken on its word. Every key must parse as an RSA
/// private key of a sane size, its fingerprint is recomputed from the key itself and must match the one declared, at most
/// one key may be active, and sizes, counts and iteration counts are capped so a crafted file cannot exhaust memory or CPU.
/// A wrong passphrase and a damaged file are indistinguishable by design.
/// </para>
/// </remarks>
public static class PluginKeyBundle
{
    public const string Format = "rustarchon-signing-keys";
    public const int Version = 1;

    public const int MinPassphraseLength = 12;
    public const int MaxPassphraseLength = 256;

    public const int Iterations = 600_000;
    private const int MinAcceptedIterations = 100_000;
    private const int MaxAcceptedIterations = 2_000_000;

    private const int SaltBytes = 16;
    private const int MinAcceptedSaltBytes = 16;
    private const int MaxAcceptedSaltBytes = 64;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>The largest bundle that is even parsed. Fifty keys of 4096 bits is well under this.</summary>
    public const int MaxBundleBytes = 128 * 1024;

    public const int MaxKeys = 50;

    private const int MinKeyBits = 2048;
    private const int MaxKeyBits = 4096;

    private const string Kdf = "PBKDF2-SHA256";

    private sealed class Container
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? ExportedAtUtc { get; set; }
        public string? Kdf { get; set; }
        public int Iterations { get; set; }
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public string? Ciphertext { get; set; }
        public List<string>? Keys { get; set; }
    }

    private sealed class Payload
    {
        public List<PayloadKey>? Keys { get; set; }
    }

    private sealed class PayloadKey
    {
        public string? Fingerprint { get; set; }
        public string? Pkcs8 { get; set; }
        public string? State { get; set; }
        public DateTimeOffset? RetiredAtUtc { get; set; }
        public DateTimeOffset? RevokedAtUtc { get; set; }
        public string? RevokedReason { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Whether a passphrase is acceptable for a new bundle.</summary>
    public static bool IsAcceptablePassphrase(string? passphrase) =>
        !string.IsNullOrEmpty(passphrase) && passphrase.Length >= MinPassphraseLength && passphrase.Length <= MaxPassphraseLength;

    /// <summary>The fingerprint of an RSA key: the first 16 hex characters of SHA-256 over its public modulus, as the plugin reports it.</summary>
    public static string FingerprintOf(RSA rsa) =>
        PluginScriptStamper.Fingerprint(Convert.ToBase64String(rsa.ExportParameters(false).Modulus!));

    /// <summary>Encrypts <paramref name="keys"/> under <paramref name="passphrase"/> into the bundle's JSON text.</summary>
    /// <exception cref="PluginKeyOperationException"><c>passphrase_weak</c>, <c>nothing_to_export</c>.</exception>
    public static string Seal(IReadOnlyList<PluginKeyBundleKey> keys, string passphrase, DateTimeOffset now)
    {
        if (!IsAcceptablePassphrase(passphrase))
        {
            throw new PluginKeyOperationException(
                "passphrase_weak", $"The passphrase must be {MinPassphraseLength} to {MaxPassphraseLength} characters.");
        }

        if (keys.Count == 0)
        {
            throw new PluginKeyOperationException("nothing_to_export", "There are no keys to export.");
        }

        var payload = new Payload
        {
            Keys = keys.Select(k => new PayloadKey
            {
                Fingerprint = k.Fingerprint,
                Pkcs8 = k.Pkcs8Base64,
                State = k.State.ToString().ToLowerInvariant(),
                RetiredAtUtc = k.RetiredAtUtc,
                RevokedAtUtc = k.RevokedAtUtc,
                RevokedReason = k.RevokedReason
            }).ToList()
        };

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var container = new Container
        {
            Format = Format,
            Version = Version,
            ExportedAtUtc = now.UtcDateTime.ToString("o"),
            Kdf = Kdf,
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Keys = keys.Select(k => k.Fingerprint).ToList()
        };

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        var secret = Derive(passphrase, salt, Iterations);
        try
        {
            using var aes = new AesGcm(secret, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(container));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        container.Tag = Convert.ToBase64String(tag);
        container.Ciphertext = Convert.ToBase64String(ciphertext);
        return JsonSerializer.Serialize(container, Json);
    }

    /// <summary>Decrypts and validates a bundle.</summary>
    /// <exception cref="PluginKeyOperationException">
    /// <c>bundle_invalid</c> (not a bundle this build understands, or malformed), <c>bundle_unreadable</c> (wrong passphrase or a
    /// damaged file - deliberately not told apart), or <c>bundle_keys_invalid</c> (a key inside does not check out).
    /// </exception>
    public static IReadOnlyList<PluginKeyBundleKey> Open(string bundleJson, string passphrase)
    {
        if (string.IsNullOrEmpty(bundleJson) || Encoding.UTF8.GetByteCount(bundleJson) > MaxBundleBytes)
        {
            throw Invalid("The file is empty or too large to be a key bundle.");
        }

        if (string.IsNullOrEmpty(passphrase) || passphrase.Length > MaxPassphraseLength)
        {
            throw Unreadable();
        }

        Container container;
        try
        {
            container = JsonSerializer.Deserialize<Container>(bundleJson, Json) ?? throw Invalid("The file is not a key bundle.");
        }
        catch (JsonException)
        {
            throw Invalid("The file is not a key bundle.");
        }

        if (container.Format != Format)
        {
            throw Invalid("The file is not a RustArchon signing key bundle.");
        }

        if (container.Version != Version)
        {
            throw Invalid($"This bundle is version {container.Version}; this Panel reads version {Version}.");
        }

        if (container.Kdf != Kdf
            || container.Iterations < MinAcceptedIterations || container.Iterations > MaxAcceptedIterations
            || container.Keys is null || container.Keys.Count is 0 or > MaxKeys)
        {
            throw Invalid("The bundle's header is not one this Panel accepts.");
        }

        byte[] salt, nonce, tag, ciphertext;
        try
        {
            salt = Convert.FromBase64String(container.Salt ?? "");
            nonce = Convert.FromBase64String(container.Nonce ?? "");
            tag = Convert.FromBase64String(container.Tag ?? "");
            ciphertext = Convert.FromBase64String(container.Ciphertext ?? "");
        }
        catch (FormatException)
        {
            throw Invalid("The bundle is damaged.");
        }

        if (salt.Length < MinAcceptedSaltBytes || salt.Length > MaxAcceptedSaltBytes
            || nonce.Length != NonceBytes || tag.Length != TagBytes
            || ciphertext.Length == 0 || ciphertext.Length > MaxBundleBytes)
        {
            throw Invalid("The bundle is damaged.");
        }

        var plaintext = new byte[ciphertext.Length];
        var secret = Derive(passphrase.Normalize(NormalizationForm.FormC), salt, container.Iterations);
        try
        {
            using var aes = new AesGcm(secret, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(container));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw Unreadable();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        try
        {
            return Validate(plaintext, container.Keys);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // What the header promises must be what the ciphertext holds: the same fingerprints, every key sound.
    private static IReadOnlyList<PluginKeyBundleKey> Validate(byte[] plaintext, List<string> declared)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(plaintext, Json);
        }
        catch (JsonException)
        {
            throw KeysInvalid("The bundle's contents are malformed.");
        }

        if (payload?.Keys is null || payload.Keys.Count == 0 || payload.Keys.Count > MaxKeys)
        {
            throw KeysInvalid("The bundle holds no usable keys.");
        }

        var result = new List<PluginKeyBundleKey>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actives = 0;
        foreach (var k in payload.Keys)
        {
            if (!TryState(k.State, out var state))
            {
                throw KeysInvalid("A key in the bundle has an unknown state.");
            }

            string actual;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(k.Pkcs8 ?? ""), out _);
                if (rsa.KeySize < MinKeyBits || rsa.KeySize > MaxKeyBits)
                {
                    throw KeysInvalid($"A key in the bundle is {rsa.KeySize} bits; only {MinKeyBits} to {MaxKeyBits} are accepted.");
                }

                actual = FingerprintOf(rsa);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                throw KeysInvalid("A key in the bundle cannot be read as an RSA private key.");
            }

            // The fingerprint is recomputed from the key, never taken from the file.
            if (!string.Equals(actual, k.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw KeysInvalid("A key in the bundle does not match the fingerprint it claims.");
            }

            if (!seen.Add(actual))
            {
                throw KeysInvalid("The bundle lists the same key twice.");
            }

            if (state == PluginKeyState.Active) { actives++; }

            result.Add(new PluginKeyBundleKey(
                actual, k.Pkcs8!, state, k.RetiredAtUtc, k.RevokedAtUtc,
                k.RevokedReason is null ? null : (k.RevokedReason.Length <= 500 ? k.RevokedReason : k.RevokedReason[..500])));
        }

        if (actives > 1)
        {
            throw KeysInvalid("The bundle marks more than one key active.");
        }

        if (!result.Select(r => r.Fingerprint).OrderBy(f => f, StringComparer.Ordinal)
                .SequenceEqual(declared.Select(d => d.ToLowerInvariant()).OrderBy(f => f, StringComparer.Ordinal)))
        {
            throw KeysInvalid("The bundle's header does not match what it holds.");
        }

        return result;
    }

    // Everything in the header that is not itself secret, in a fixed order, so it is authenticated with the ciphertext.
    private static byte[] AssociatedData(Container c) => Encoding.UTF8.GetBytes(string.Join(
        "|", c.Format, c.Version, c.ExportedAtUtc, c.Kdf, c.Iterations, c.Salt, c.Nonce,
        string.Join(",", (c.Keys ?? []).Select(k => k.ToLowerInvariant()))));

    private static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static bool TryState(string? text, out PluginKeyState state)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "active": state = PluginKeyState.Active; return true;
            case "retired": state = PluginKeyState.Retired; return true;
            case "revoked": state = PluginKeyState.Revoked; return true;
            default: state = default; return false;
        }
    }

    private static PluginKeyOperationException Invalid(string message) => new("bundle_invalid", message);

    private static PluginKeyOperationException Unreadable() =>
        new("bundle_unreadable", "The file could not be opened: the passphrase is wrong, or the file has been changed or damaged.");

    private static PluginKeyOperationException KeysInvalid(string message) => new("bundle_keys_invalid", message);
}
