// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

using System;
using System.Security.Cryptography;
using System.Text;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default AES-GCM-256 implementation of <see cref="ISecretsEncryptor"/>.
/// The key is loaded once at construction from (in priority order):
/// the <c>SPRING_SECRETS_AES_KEY</c> environment variable (base64) or the
/// <see cref="SecretsOptions.AesKeyFile"/> path (base64 contents). One of
/// the two MUST be configured — there is no in-memory fallback. The
/// previous "ephemeral dev key" path was removed because a per-process
/// random key cannot work in the platform's multi-process topology
/// (spring-api / spring-worker share the same encrypted secret store);
/// missing-key now fails loudly at host start instead of silently
/// corrupting cross-process secret reads.
///
/// <para>
/// Envelope layout (version 1, AES-GCM-256):
/// <c>[0x01][nonce(12)][ciphertext(N)][tag(16)]</c>. The whole byte
/// string is then base64-encoded for storage as a plain string value.
/// Associated data is <c>"{tenantId}:{storeKey}"</c> — rebinding a
/// ciphertext to a different tuple makes authentication fail.
/// </para>
/// </summary>
public partial class SecretsEncryptor : ISecretsEncryptor
{
    /// <summary>
    /// Environment variable name that carries the base64-encoded AES key.
    /// </summary>
    public const string KeyEnvironmentVariable = "SPRING_SECRETS_AES_KEY";

    /// <summary>
    /// AES-256 key size in bytes.
    /// </summary>
    public const int KeySize = 32;

    /// <summary>
    /// AES-GCM nonce size in bytes.
    /// </summary>
    public const int NonceSize = 12;

    /// <summary>
    /// AES-GCM authentication tag size in bytes.
    /// </summary>
    public const int TagSize = 16;

    /// <summary>
    /// Envelope version byte for AES-GCM-256 with (tenantId, storeKey) AAD.
    /// </summary>
    public const byte Version1 = 0x01;

    private readonly byte[] _key;
    private readonly ILogger<SecretsEncryptor> _logger;

    /// <summary>
    /// Creates a new <see cref="SecretsEncryptor"/>. Fails fast if no key
    /// source is configured and the dev-key fallback is disabled, or if
    /// the configured key is obviously weak.
    /// </summary>
    public SecretsEncryptor(
        IOptions<SecretsOptions> options,
        ILogger<SecretsEncryptor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _key = LoadKey(options.Value, logger);
    }

    /// <inheritdoc />
    public string Encrypt(string plaintext, Guid tenantId, string storeKey)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var aad = BuildAad(tenantId, storeKey);

        // [version][nonce(12)][ciphertext(N)][tag(16)]
        var envelope = new byte[1 + NonceSize + plaintextBytes.Length + TagSize];
        envelope[0] = Version1;
        var nonceSpan = envelope.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonceSpan);

        var ciphertextSpan = envelope.AsSpan(1 + NonceSize, plaintextBytes.Length);
        var tagSpan = envelope.AsSpan(1 + NonceSize + plaintextBytes.Length, TagSize);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonceSpan, plaintextBytes, ciphertextSpan, tagSpan, aad);

        return Convert.ToBase64String(envelope);
    }

    /// <inheritdoc />
    public string Decrypt(string value, Guid tenantId, string storeKey, out bool wasEnveloped)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        // Legacy path: if the value is not a valid base64 envelope with
        // the version prefix, treat it as pre-encryption plaintext.
        if (!TryDecodeEnvelope(value, out var envelope))
        {
            wasEnveloped = false;
            return value;
        }

        var version = envelope[0];
        if (version != Version1)
        {
            throw new InvalidOperationException(
                $"Unsupported secrets envelope version 0x{version:X2}. " +
                "This runtime only knows version 0x01 (AES-GCM-256).");
        }

        if (envelope.Length < 1 + NonceSize + TagSize)
        {
            throw new InvalidOperationException(
                "Secrets envelope too short to contain nonce, ciphertext, and tag.");
        }

        var nonce = envelope.AsSpan(1, NonceSize);
        var cipherLength = envelope.Length - 1 - NonceSize - TagSize;
        var ciphertext = envelope.AsSpan(1 + NonceSize, cipherLength);
        var tag = envelope.AsSpan(1 + NonceSize + cipherLength, TagSize);

        var aad = BuildAad(tenantId, storeKey);
        var plaintext = new byte[cipherLength];

        using var aes = new AesGcm(_key, TagSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            // Wrap in a domain exception so callers can distinguish
            // "can't decrypt this slot" from generic infrastructure errors.
            // Don't leak which AAD mismatched — the message only indicates
            // that the envelope didn't authenticate.
            throw new SecretUnreadableException(
                "Failed to authenticate secrets envelope. " +
                "Either the key material changed, the ciphertext was transplanted " +
                "across a different (tenantId, storeKey) pair, or the envelope is corrupted.",
                ex);
        }

        wasEnveloped = true;
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] BuildAad(Guid tenantId, string storeKey)
        => Encoding.UTF8.GetBytes($"{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(tenantId)}:{storeKey}");

    private static bool TryDecodeEnvelope(string value, out byte[] envelope)
    {
        envelope = Array.Empty<byte>();

        // A legacy plaintext value is very likely either not valid base64
        // or, if it happens to be, its first byte won't match our version
        // marker. Either way we fall back to the legacy interpretation.
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length < 1 + NonceSize + TagSize)
        {
            return false;
        }

        if (decoded[0] != Version1)
        {
            return false;
        }

        envelope = decoded;
        return true;
    }

    private static byte[] LoadKey(SecretsOptions options, ILogger logger)
    {
        // Classification logic is shared with SecretsConfigurationRequirement
        // via SecretsKeyClassifier so the requirement report and the
        // encryptor self-check cannot drift. When the configuration
        // validation framework is wired in, the requirement catches every
        // non-happy-path case at host start — we still check here as
        // defense in depth for hosts that bypass the validator.
        var result = SecretsKeyClassifier.Classify(options);
        switch (result.Kind)
        {
            case SecretsKeySource.EnvironmentVariable:
                Log.UsingEnvKey(logger);
                return result.Key!;

            case SecretsKeySource.File:
                Log.UsingFileKey(logger, options.AesKeyFile!);
                return result.Key!;

            case SecretsKeySource.MissingFile:
                throw new InvalidOperationException(
                    result.Reason + " " + SecretsKeyClassifier.BuildKeySourceHelp());

            case SecretsKeySource.Malformed:
            case SecretsKeySource.WeakKey:
                throw new InvalidOperationException(
                    result.Reason + " Refusing to start.");

            case SecretsKeySource.NotConfigured:
            default:
                throw new InvalidOperationException(
                    (result.Reason ?? "Spring secrets at-rest encryption is required but no key is configured.") +
                    " " + SecretsKeyClassifier.BuildKeySourceHelp());
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 2410,
            Level = LogLevel.Information,
            Message = "Loaded Spring secrets AES key from environment variable SPRING_SECRETS_AES_KEY.")]
        public static partial void UsingEnvKey(ILogger logger);

        [LoggerMessage(
            EventId = 2411,
            Level = LogLevel.Information,
            Message = "Loaded Spring secrets AES key from file '{Path}'.")]
        public static partial void UsingFileKey(ILogger logger, string path);

    }
}
