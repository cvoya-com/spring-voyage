// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Delegates credential validation (and, where the CLI supports it,
/// model enumeration) to a provider's locally-installed command-line
/// tool rather than the provider's REST API. Introduced in #660 so
/// Claude.ai OAuth tokens — which the Anthropic REST endpoint rejects
/// because they're not Platform-plan API keys — can be validated
/// through the <c>claude</c> CLI, which accepts both formats
/// transparently.
/// </summary>
/// <remarks>
/// <para>
/// Each provider registers its own implementation (keyed by
/// <see cref="ProviderId"/>). <see cref="IProviderCredentialValidator"/>
/// consults the registered invokers first; if one reports the CLI is
/// installed, it owns the validation. Otherwise the validator falls
/// back to its REST path — and for credential formats that only the
/// CLI can validate (e.g. Claude.ai OAuth tokens), returns a clear
/// error instead of silently issuing a REST call guaranteed to fail.
/// </para>
/// <para>
/// Interface lives in <c>Cvoya.Spring.Core</c> so the private cloud
/// host can swap in a tenant-scoped invoker (e.g. one that proxies the
/// CLI call through an egress worker) via DI without touching the
/// open-source validator.
/// </para>
/// </remarks>
public interface IProviderCliInvoker
{
    /// <summary>
    /// The canonical provider id this invoker services (e.g.
    /// <c>"anthropic"</c>, <c>"openai"</c>). The validator looks up
    /// invokers by normalised provider id; unknown providers skip the
    /// CLI path entirely.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Reports whether the provider's CLI is installed on this host.
    /// Called before <see cref="ValidateAsync"/> so the validator can
    /// decide between the CLI path, the REST fallback, and the "OAuth
    /// token without CLI" error path.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates <paramref name="credential"/> by invoking the provider's
    /// CLI with that credential.
    /// </summary>
    /// <param name="credential">
    /// The plaintext credential — whatever format the CLI accepts
    /// (API key, OAuth token, etc.). The invoker hands it to the CLI
    /// via the documented env var; the credential never touches disk,
    /// the command line, or logs.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the in-flight CLI invocation. Implementations MUST
    /// terminate the spawned process on cancellation.
    /// </param>
    Task<ProviderCliValidationResult> ValidateAsync(
        string credential,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a CLI-based credential validation.
/// </summary>
/// <param name="Status">Coarse-grained verdict bucket.</param>
/// <param name="Models">
/// Live model ids reported by the CLI when <see cref="Status"/> is
/// <see cref="ProviderCredentialValidationStatus.Valid"/> AND the CLI
/// supports model enumeration. <c>null</c> when the CLI cannot enumerate
/// (the validator then falls back to a static curated list).
/// </param>
/// <param name="ErrorMessage">
/// Human-readable failure reason, already phrased for operators, when
/// <see cref="Status"/> is not <see cref="ProviderCredentialValidationStatus.Valid"/>.
/// </param>
public record ProviderCliValidationResult(
    ProviderCredentialValidationStatus Status,
    IReadOnlyList<string>? Models,
    string? ErrorMessage);