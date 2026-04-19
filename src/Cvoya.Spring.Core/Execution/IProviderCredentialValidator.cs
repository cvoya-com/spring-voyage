// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Validates an LLM-provider credential (Anthropic, OpenAI, Google) by
/// issuing a lightweight read-only call against the provider's own API —
/// typically the <c>GET /v1/models</c> endpoint. Backs the unit-creation
/// wizard's credential-entry step (#655) so operators learn about a typo
/// before advancing through the wizard, not at the first agent-dispatch
/// attempt.
/// </summary>
/// <remarks>
/// <para>
/// The validator never touches stored secrets — it only validates a
/// plaintext value supplied by the caller. This is the seam the wizard
/// hits right after the operator types a key and the seam the CLI will
/// hit when <c>spring secret create --validate</c> lands.
/// </para>
/// <para>
/// Implementations must return success + the live model list whenever
/// the provider call succeeds, so the wizard can seed the Model dropdown
/// from the same call it used to validate the key (no round-trip through
/// <see cref="IModelCatalog"/> needed). When the provider has no
/// list-models endpoint, implementations may return success with a
/// <c>null</c> or empty model list.
/// </para>
/// </remarks>
public interface IProviderCredentialValidator
{
    /// <summary>
    /// Validates the supplied API key against <paramref name="providerId"/>.
    /// </summary>
    /// <param name="providerId">
    /// Canonical provider identifier — <c>claude</c>/<c>anthropic</c>,
    /// <c>openai</c>, <c>google</c>/<c>gemini</c>. Unknown provider ids
    /// return <see cref="ProviderCredentialValidationStatus.UnknownProvider"/>.
    /// </param>
    /// <param name="apiKey">
    /// The plaintext API key to validate. Blank or whitespace-only keys
    /// return <see cref="ProviderCredentialValidationStatus.MissingKey"/>
    /// without issuing an HTTP request.
    /// </param>
    /// <param name="cancellationToken">Cancels an in-flight provider call.</param>
    /// <returns>
    /// A <see cref="ProviderCredentialValidationResult"/> describing
    /// success + discovered model ids, or the specific failure mode so
    /// the caller can surface an operator-facing message.
    /// </returns>
    Task<ProviderCredentialValidationResult> ValidateAsync(
        string providerId,
        string apiKey,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a credential validation call.</summary>
/// <param name="Status">Coarse-grained result bucket.</param>
/// <param name="Models">
/// The provider's reported model ids when <see cref="Status"/> is
/// <see cref="ProviderCredentialValidationStatus.Valid"/>; <c>null</c>
/// otherwise. May be empty when the provider returned zero entries.
/// </param>
/// <param name="ErrorMessage">
/// Human-readable reason when validation failed. Already phrased for
/// operators — the wizard can surface it verbatim in an inline banner.
/// </param>
public record ProviderCredentialValidationResult(
    ProviderCredentialValidationStatus Status,
    IReadOnlyList<string>? Models,
    string? ErrorMessage);

/// <summary>Coarse-grained outcome of a credential validation call.</summary>
public enum ProviderCredentialValidationStatus
{
    /// <summary>The provider accepted the key and (when applicable) returned a model list.</summary>
    Valid = 0,

    /// <summary>The provider rejected the key (HTTP 401 / 403).</summary>
    Unauthorized = 1,

    /// <summary>The provider responded but with an unexpected status; likely a transient outage.</summary>
    ProviderError = 2,

    /// <summary>The validator couldn't reach the provider (DNS, timeout, network).</summary>
    NetworkError = 3,

    /// <summary>The caller supplied a blank or whitespace-only key.</summary>
    MissingKey = 4,

    /// <summary>The validator has no route for the supplied <c>providerId</c>.</summary>
    UnknownProvider = 5,
}