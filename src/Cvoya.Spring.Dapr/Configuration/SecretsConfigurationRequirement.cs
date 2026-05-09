// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Configuration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Options;

/// <summary>
/// Tier-1 requirement: the AES-256 key source used by
/// <see cref="SecretsEncryptor"/> to envelope-encrypt platform secrets.
/// Mandatory — the platform refuses to start without a usable key source.
/// </summary>
/// <remarks>
/// <para>
/// Classification is delegated to <see cref="SecretsKeyClassifier"/>, which
/// also drives the encryptor constructor. Keeping one classifier means the
/// requirement report and the encryptor self-check cannot drift on what
/// counts as "missing", "malformed", or "weak".
/// </para>
/// <para>
/// <b>Status mapping.</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="SecretsKeySource.EnvironmentVariable"/> or <see cref="SecretsKeySource.File"/> → <see cref="ConfigurationStatus.Met"/>.</item>
///   <item>Every failure classification (<see cref="SecretsKeySource.NotConfigured"/>,
///   <see cref="SecretsKeySource.MissingFile"/>, <see cref="SecretsKeySource.Malformed"/>,
///   <see cref="SecretsKeySource.WeakKey"/>) → <see cref="ConfigurationStatus.Invalid"/> with a fatal error.</item>
/// </list>
/// <para>
/// Replaces the <see cref="InvalidOperationException"/> throws that used to
/// surface at first <see cref="SecretsEncryptor"/> construction (lazy,
/// because the encryptor is a singleton resolved on first secret touch).
/// The requirement lifts the same classification to host start so operators
/// see the failure in the same fail-fast moment as a bad DB connection
/// string.
/// </para>
/// </remarks>
public sealed class SecretsConfigurationRequirement(
    IOptions<SecretsOptions> optionsAccessor) : IConfigurationRequirement
{
    /// <inheritdoc />
    public string RequirementId => "secrets-encryption-key";

    /// <inheritdoc />
    public string DisplayName => "Secrets encryption key";

    /// <inheritdoc />
    public string SubsystemName => "Secrets";

    /// <inheritdoc />
    public bool IsMandatory => true;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { SecretsKeyClassifier.KeyEnvironmentVariable, "Secrets__AesKeyFile" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => SecretsOptions.SectionName;

    /// <inheritdoc />
    public string Description =>
        "AES-256 key used to envelope-encrypt platform secrets at rest. Sourced (in priority order) from SPRING_SECRETS_AES_KEY or the Secrets:AesKeyFile path. One of the two is required — the platform refuses to start without a usable key source so cross-process secrets reads stay decryptable.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/developer/secret-store.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var result = SecretsKeyClassifier.Classify(optionsAccessor.Value);

        switch (result.Kind)
        {
            case SecretsKeySource.EnvironmentVariable:
            case SecretsKeySource.File:
                return Task.FromResult(ConfigurationRequirementStatus.Met());

            case SecretsKeySource.NotConfigured:
                {
                    var reason = result.Reason ?? "No Spring secrets AES key configured.";
                    var suggestion = SecretsKeyClassifier.BuildKeySourceHelp();
                    return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                        reason: reason,
                        suggestion: suggestion,
                        fatalError: new InvalidOperationException(reason + " " + suggestion)));
                }

            case SecretsKeySource.MissingFile:
                {
                    var reason = result.Reason ?? "Secrets:AesKeyFile points to a missing file.";
                    var suggestion = SecretsKeyClassifier.BuildKeySourceHelp();
                    return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                        reason: reason,
                        suggestion: suggestion,
                        fatalError: new InvalidOperationException(reason + " " + suggestion)));
                }

            case SecretsKeySource.Malformed:
                {
                    var reason = result.Reason ?? "Spring secrets AES key is malformed.";
                    var suggestion =
                        "Replace the key material with a base64-encoded 32-byte (256-bit) random key. " +
                        "Example: `openssl rand -base64 32`.";
                    return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                        reason: reason,
                        suggestion: suggestion,
                        fatalError: new InvalidOperationException(reason + " Refusing to start.")));
                }

            case SecretsKeySource.WeakKey:
                {
                    var reason = result.Reason ?? "Spring secrets AES key matches a weak-key sentinel pattern.";
                    var suggestion =
                        "Replace the key material with a fresh random 32-byte key. Example: `openssl rand -base64 32`.";
                    return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                        reason: reason,
                        suggestion: suggestion,
                        fatalError: new InvalidOperationException(reason + " Refusing to start.")));
                }

            default:
                return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                    reason: $"Unexpected secrets key classification '{result.Kind}'.",
                    suggestion: "File an issue on the Spring Voyage repo — the classifier returned an unrecognised value.",
                    fatalError: new InvalidOperationException(
                        $"Unexpected secrets key classification '{result.Kind}'.")));
        }
    }
}
