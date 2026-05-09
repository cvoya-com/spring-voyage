// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

using System;
using System.IO;
using System.Text;

using Cvoya.Spring.Dapr.Tenancy;

/// <summary>
/// Shared classification helper for the Spring secrets AES-256 key source.
/// Used by both <see cref="SecretsEncryptor"/> (fails the constructor when the
/// result is not usable) and <c>SecretsConfigurationRequirement</c> (renders
/// the result into a <c>ConfigurationRequirementStatus</c> at host start).
/// </summary>
/// <remarks>
/// The classifier does not throw — it returns a <see cref="SecretsKeySourceResult"/>
/// carrying the key bytes (when valid) plus a classification enum and a
/// human-readable reason. Callers decide how to surface the outcome.
/// </remarks>
public static class SecretsKeyClassifier
{
    /// <summary>
    /// Environment variable name that carries the base64-encoded AES key.
    /// Kept in sync with <see cref="SecretsEncryptor.KeyEnvironmentVariable"/>.
    /// </summary>
    public const string KeyEnvironmentVariable = SecretsEncryptor.KeyEnvironmentVariable;

    /// <summary>
    /// AES-256 key size in bytes. Kept in sync with <see cref="SecretsEncryptor.KeySize"/>.
    /// </summary>
    public const int KeySize = SecretsEncryptor.KeySize;

    /// <summary>
    /// Classify the configured secrets key source from the process environment
    /// and the supplied <see cref="SecretsOptions"/>.
    /// </summary>
    public static SecretsKeySourceResult Classify(SecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 1) SPRING_SECRETS_AES_KEY environment variable takes priority.
        var envValue = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return ClassifyEncoded(envValue!, $"environment variable {KeyEnvironmentVariable}", SecretsKeySource.EnvironmentVariable);
        }

        // 2) Optional key file path from Secrets:AesKeyFile.
        if (!string.IsNullOrWhiteSpace(options.AesKeyFile))
        {
            var path = options.AesKeyFile!;
            if (!File.Exists(path))
            {
                return new SecretsKeySourceResult(
                    SecretsKeySource.MissingFile,
                    Key: null,
                    Source: $"file '{path}'",
                    Reason: $"Secrets:AesKeyFile is configured ({path}) but the file does not exist.");
            }

            string contents;
            try
            {
                contents = File.ReadAllText(path).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SecretsKeySourceResult(
                    SecretsKeySource.MissingFile,
                    Key: null,
                    Source: $"file '{path}'",
                    Reason: $"Secrets:AesKeyFile ({path}) could not be read: {ex.Message}");
            }

            return ClassifyEncoded(contents, $"file '{path}'", SecretsKeySource.File);
        }

        // 3) No key source configured. The OSS deployment requires a real
        // key (env var or file) so secrets remain readable across process
        // restarts AND across the platform's multi-process topology
        // (spring-api / spring-worker share the same encrypted secret
        // store; a per-process random key would corrupt every cross-app
        // read). The previous "ephemeral dev key" path was removed for
        // exactly that reason — see CHANGELOG entry under v0.1.
        return new SecretsKeySourceResult(
            SecretsKeySource.NotConfigured,
            Key: null,
            Source: null,
            Reason: "Spring secrets at-rest encryption is required but no key is configured.");
    }

    private static SecretsKeySourceResult ClassifyEncoded(string encoded, string source, SecretsKeySource successKind)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return new SecretsKeySourceResult(
                SecretsKeySource.Malformed,
                Key: null,
                Source: source,
                Reason: $"Spring secrets AES key from {source} is not valid base64. Provide a base64-encoded 32-byte (256-bit) key.");
        }

        if (key.Length != KeySize)
        {
            return new SecretsKeySourceResult(
                SecretsKeySource.Malformed,
                Key: null,
                Source: source,
                Reason: $"Spring secrets AES key from {source} is {key.Length} bytes; must be {KeySize} bytes (AES-256).");
        }

        if (IsAllSameByte(key, 0x00))
        {
            return new SecretsKeySourceResult(
                SecretsKeySource.WeakKey,
                Key: null,
                Source: source,
                Reason: $"Spring secrets AES key from {source} is all zeros.");
        }

        if (IsAllSameByte(key, 0xFF))
        {
            return new SecretsKeySourceResult(
                SecretsKeySource.WeakKey,
                Key: null,
                Source: source,
                Reason: $"Spring secrets AES key from {source} is all 0xFF.");
        }

        if (IsSentinelPattern(key))
        {
            return new SecretsKeySourceResult(
                SecretsKeySource.WeakKey,
                Key: null,
                Source: source,
                Reason: $"Spring secrets AES key from {source} matches a known sentinel/test pattern.");
        }

        return new SecretsKeySourceResult(
            successKind,
            Key: key,
            Source: source,
            Reason: null);
    }

    private static bool IsAllSameByte(byte[] bytes, byte target)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != target)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSentinelPattern(byte[] key)
    {
        // 0x00, 0x01, 0x02 ... ascending — commonly seen in copy-pasted examples.
        var ascending = true;
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] != (byte)i)
            {
                ascending = false;
                break;
            }
        }
        if (ascending)
        {
            return true;
        }

        // "changeme" / "test" repeated ASCII.
        var ascii = Encoding.ASCII.GetString(key);
        if (ascii.StartsWith("changeme", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (ascii.StartsWith("testtest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // All-ASCII-space or all-"A" is also obviously weak.
        if (IsAllSameByte(key, 0x20) || IsAllSameByte(key, (byte)'A'))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Shared operator-facing help string recommending the supported key
    /// sources. Included in messages from both the encryptor and the
    /// configuration requirement.
    /// </summary>
    public static string BuildKeySourceHelp() =>
        "Configure one of: " +
        $"(1) the {KeyEnvironmentVariable} environment variable with a base64-encoded 32-byte key, " +
        "or (2) Secrets:AesKeyFile pointing to a file whose contents are a base64-encoded 32-byte key. " +
        "Generate a key with: openssl rand -base64 32. " +
        "Production deployments should externalize key material via a KMS-backed ISecretStore " +
        "implementation rather than relying on this at-rest layer alone.";
}

/// <summary>
/// Classification of the configured secrets key source.
/// </summary>
public enum SecretsKeySource
{
    /// <summary>Key supplied via the <c>SPRING_SECRETS_AES_KEY</c> environment variable and validated.</summary>
    EnvironmentVariable,

    /// <summary>Key supplied via <c>Secrets:AesKeyFile</c> and validated.</summary>
    File,

    /// <summary>No key source configured.</summary>
    NotConfigured,

    /// <summary><c>Secrets:AesKeyFile</c> points to a missing or unreadable path.</summary>
    MissingFile,

    /// <summary>Key material could not be decoded as base64, or decoded to the wrong length.</summary>
    Malformed,

    /// <summary>Key decoded correctly but matches a known sentinel / all-zero / all-0xFF pattern.</summary>
    WeakKey,
}

/// <summary>
/// Outcome of <see cref="SecretsKeyClassifier.Classify"/>. When
/// <paramref name="Kind"/> is <see cref="SecretsKeySource.EnvironmentVariable"/>
/// or <see cref="SecretsKeySource.File"/>, <paramref name="Key"/> is populated;
/// every other value leaves <paramref name="Key"/> <c>null</c>.
/// </summary>
/// <param name="Kind">Classification of the key source.</param>
/// <param name="Key">Decoded 32-byte AES key when the source is valid; <c>null</c> otherwise.</param>
/// <param name="Source">Short operator-facing label identifying the source ("environment variable …", "file '…'"). May be <c>null</c> when no source is configured.</param>
/// <param name="Reason">Human-readable reason describing why the source is not usable. <c>null</c> for the happy path.</param>
public sealed record SecretsKeySourceResult(
    SecretsKeySource Kind,
    byte[]? Key,
    string? Source,
    string? Reason);
