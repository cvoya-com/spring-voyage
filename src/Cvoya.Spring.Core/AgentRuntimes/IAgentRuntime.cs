// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Describes an agent runtime — a plugin bundling an execution tool
/// (e.g. <c>claude-code-cli</c>, <c>codex-cli</c>, <c>dapr-agent</c>) with a
/// compatible LLM backend, its credential schema, and its supported model
/// catalog. The API layer, wizard, and CLI consume this abstraction via
/// dependency injection and never import any concrete runtime package, so a
/// new runtime lands by registering one more <see cref="IAgentRuntime"/>
/// implementation in DI and shipping its library alongside.
/// </summary>
/// <remarks>
/// <para>
/// Each runtime is identified by a stable <see cref="Id"/> (persisted with
/// every tenant install and unit binding so a display-name change never
/// breaks existing data) and a human-readable <see cref="DisplayName"/>.
/// Lookups on <see cref="IAgentRuntimeRegistry"/> are case-insensitive on
/// <see cref="Id"/>.
/// </para>
/// <para>
/// The <see cref="ToolKind"/> groups runtimes by the execution tool they use
/// (e.g. multiple runtimes may share <c>dapr-agent</c>). This lets the host
/// reason about container baseline requirements without knowing the full
/// runtime list.
/// </para>
/// <para>
/// Implementations declare the expected credential shape via
/// <see cref="CredentialSchema"/> and validate a candidate credential with
/// <see cref="ValidateCredentialAsync"/>. The host uses both at wizard
/// accept-time and again at runtime via the credential-health store.
/// </para>
/// <para>
/// <see cref="DefaultModels"/> is the seed catalog shipped with the runtime
/// (loaded from the runtime's <c>agent-runtimes/&lt;id&gt;/seed.json</c>
/// file). Tenants may override or extend this list via per-tenant install
/// configuration; this contract only exposes the out-of-the-box defaults.
/// </para>
/// <para>
/// <see cref="VerifyContainerBaselineAsync"/> checks whether the runtime's
/// required tooling is available in the current process/container (for
/// example, that the <c>claude</c> CLI binary is on PATH). The wizard and
/// install flow call this to surface environment drift before a unit tries
/// to run.
/// </para>
/// </remarks>
public interface IAgentRuntime
{
    /// <summary>
    /// Stable identity for this runtime (e.g. <c>claude</c>, <c>openai</c>).
    /// Persisted in tenant installs and unit bindings. Lookups on
    /// <see cref="IAgentRuntimeRegistry"/> are case-insensitive against this
    /// value.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-facing display name for UI/CLI surfaces.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Identifier for the execution tool this runtime uses — for example
    /// <c>claude-code-cli</c>, <c>codex-cli</c>, or <c>dapr-agent</c>. Two
    /// distinct runtimes may share the same tool kind if they differ only in
    /// the LLM backend they target.
    /// </summary>
    string ToolKind { get; }

    /// <summary>
    /// Describes the credential shape the runtime expects (API key, OAuth
    /// token, or none) together with an optional display hint for the
    /// wizard credential input.
    /// </summary>
    AgentRuntimeCredentialSchema CredentialSchema { get; }

    /// <summary>
    /// Validates a candidate credential against the runtime's backing
    /// service. Used at wizard accept-time and by the credential-health
    /// store. Implementations should surface transport-level failures as
    /// <see cref="CredentialValidationStatus.NetworkError"/> rather than
    /// throwing.
    /// </summary>
    /// <param name="credential">The raw credential to validate (API key, OAuth token, or empty when the schema requires no credential).</param>
    /// <param name="cancellationToken">A token to cancel the validation.</param>
    Task<CredentialValidationResult> ValidateCredentialAsync(string credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// The seed model catalog shipped with the runtime. Tenants may override
    /// or extend this list via per-tenant install configuration; this
    /// property only exposes the out-of-the-box defaults (loaded from the
    /// runtime's seed file).
    /// </summary>
    IReadOnlyList<ModelDescriptor> DefaultModels { get; }

    /// <summary>
    /// Checks whether the runtime's required tooling is present in the
    /// current process/container — for example, that the <c>claude</c> CLI
    /// binary is on PATH. Surfaced in the wizard and install flow so
    /// environment drift is visible before a unit tries to run.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    Task<ContainerBaselineCheckResult> VerifyContainerBaselineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort fetch of the runtime's live model catalog from its
    /// backing service (typically via the provider's <c>/v1/models</c> or
    /// equivalent endpoint). Used by the tenant install's
    /// <c>POST /api/v1/agent-runtimes/{id}/refresh-models</c> path to
    /// reconcile the tenant's stored model list with what the provider
    /// currently publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations should surface transport-level failures as
    /// <see cref="FetchLiveModelsStatus.NetworkError"/> and rejected
    /// credentials as <see cref="FetchLiveModelsStatus.InvalidCredential"/>
    /// rather than throwing. Runtimes whose backing service does not
    /// expose a model-enumeration endpoint (for example, runtimes that
    /// only speak a single hard-coded model) MUST return
    /// <see cref="FetchLiveModelsStatus.Unsupported"/> so callers can
    /// keep the seed catalog as the authoritative list.
    /// </para>
    /// <para>
    /// The <paramref name="credential"/> is the raw credential the
    /// caller supplies — typically the tenant's configured API key.
    /// Runtimes that authenticate via schemes other than
    /// <see cref="AgentRuntimeCredentialKind.ApiKey"/> (for example,
    /// Ollama's credential-less local endpoint) should ignore it.
    /// </para>
    /// </remarks>
    /// <param name="credential">The raw credential to present to the backing service. Empty when the runtime requires no credential.</param>
    /// <param name="cancellationToken">A token to cancel the fetch.</param>
    Task<FetchLiveModelsResult> FetchLiveModelsAsync(string credential, CancellationToken cancellationToken = default);
}