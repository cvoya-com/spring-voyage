// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Describes an agent runtime — a plugin bundling an execution tool
/// (e.g. <c>claude-code-cli</c>, <c>codex-cli</c>, <c>spring-voyage</c>) with a
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
/// (e.g. multiple runtimes may share <c>spring-voyage</c>). This lets the host
/// reason about container baseline requirements without knowing the full
/// runtime list.
/// </para>
/// <para>
/// Implementations declare the expected credential shape via
/// <see cref="CredentialSchema"/>. V2 has moved unit-validation to the
/// backend: the runtime produces a declarative list of in-container probe
/// commands via
/// <see cref="GetProbeSteps(AgentRuntimeInstallConfig, string)"/> and the
/// Dapr <c>UnitValidationWorkflow</c> runs them inside the unit's chosen
/// container image. The runtime never shells out on the host for
/// credential or tool-baseline checks.
/// </para>
/// <para>
/// <see cref="DefaultModels"/> is the seed catalog shipped with the runtime
/// (loaded from the runtime's <c>agent-runtimes/&lt;id&gt;/seed.json</c>
/// file). Tenants may override or extend this list via per-tenant install
/// configuration; this contract only exposes the out-of-the-box defaults.
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
    /// <c>claude-code-cli</c>, <c>codex-cli</c>, or <c>spring-voyage</c>. Two
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
    /// The canonical secret name under which this runtime's credential is
    /// stored (for example, <c>anthropic-api-key</c>, <c>openai-api-key</c>,
    /// <c>google-api-key</c>). The tier-2
    /// <see cref="Cvoya.Spring.Core.Execution.ILlmCredentialResolver"/>
    /// reads this value from the registry so the provider-id → secret-name
    /// mapping lives with the runtime plugin rather than on a host-side
    /// switch, and the tenant-defaults portal surface the same string
    /// everywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtimes whose <see cref="CredentialSchema"/> is
    /// <see cref="AgentRuntimeCredentialKind.None"/> (for example Ollama)
    /// MUST return <see cref="string.Empty"/> — the resolver treats an
    /// empty name as "no credential to look up" and returns
    /// <see cref="Cvoya.Spring.Core.Execution.LlmCredentialSource.NotFound"/>
    /// without consulting the secret store.
    /// </para>
    /// <para>
    /// The value is stable: it is persisted in tenant/unit-scoped secret
    /// rows and documented in <c>docs/guide/secrets.md</c>. Renaming it
    /// would orphan every existing secret, so treat it as immutable.
    /// </para>
    /// </remarks>
    string CredentialSecretName { get; }

    /// <summary>
    /// The container environment variable that the runtime's launcher
    /// injects with the resolved credential value (for example
    /// <c>CLAUDE_CODE_OAUTH_TOKEN</c>, <c>OPENAI_API_KEY</c>,
    /// <c>GOOGLE_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>). The launcher
    /// reads this value from the runtime so the runtime → env-var
    /// mapping lives next to the runtime plugin rather than duplicated
    /// across launchers and the daprd sidecar env-propagation path
    /// (#1714 step 1–3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtimes whose <see cref="CredentialSchema"/> is
    /// <see cref="AgentRuntimeCredentialKind.None"/> (for example Ollama)
    /// MUST return <see cref="string.Empty"/> — the launcher treats an
    /// empty env-var name as "no credential to inject" and skips both
    /// the resolver call and the env-var write.
    /// </para>
    /// <para>
    /// The strict per-path matrix (#1714) means the env var the launcher
    /// emits is fixed for that path: the Claude agent-runtime path emits
    /// <c>CLAUDE_CODE_OAUTH_TOKEN</c>; the Spring Voyage REST path emits
    /// <c>ANTHROPIC_API_KEY</c>. Each path accepts exactly one credential
    /// shape, so there is no shape-branching at the launcher.
    /// </para>
    /// </remarks>
    string CredentialEnvVar { get; }

    /// <summary>
    /// The seed model catalog shipped with the runtime. Tenants may override
    /// or extend this list via per-tenant install configuration; this
    /// property only exposes the out-of-the-box defaults (loaded from the
    /// runtime's seed file).
    /// </summary>
    IReadOnlyList<ModelDescriptor> DefaultModels { get; }

    /// <summary>
    /// The default container image the portal wizard pre-fills when the
    /// operator selects this runtime. The image should contain the runtime's
    /// CLI tool pre-installed so units can start without a custom image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wizard applies this value the first time the operator picks a
    /// runtime (while the image field still holds the base-image placeholder).
    /// Once the operator edits the image field manually, subsequent runtime
    /// changes do not overwrite it.
    /// </para>
    /// <para>
    /// The value must be a non-null, non-empty image reference. It does not
    /// have to be currently published — the backend validates availability at
    /// unit-validation time.
    /// </para>
    /// </remarks>
    string DefaultImage { get; }

    /// <summary>
    /// Builds the declarative list of in-container probe commands the
    /// Dapr <c>UnitValidationWorkflow</c> should execute against the unit's
    /// chosen container image, after pulling the image and starting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by the workflow to produce the list of in-container probe
    /// commands. The workflow pulls the image, then executes each returned
    /// step inside the image and invokes
    /// <see cref="ProbeStep.InterpretOutput"/> on the
    /// <c>(exitCode, stdout, stderr)</c> triple. The returned list is
    /// ordered — the workflow runs the steps in sequence and stops on the
    /// first failure so later steps (for example,
    /// <see cref="Cvoya.Spring.Core.Units.UnitValidationStep.ResolvingModel"/>)
    /// do not run against a credential that has already been rejected.
    /// </para>
    /// <para>
    /// <see cref="Cvoya.Spring.Core.Units.UnitValidationStep.PullingImage"/>
    /// is the dispatcher's concern and MUST NOT appear in the returned
    /// list. Every step is an in-container exec; host-side shelling out is
    /// forbidden.
    /// </para>
    /// <para>
    /// Runtimes whose <see cref="CredentialSchema"/> is
    /// <see cref="AgentRuntimeCredentialKind.None"/> (for example Ollama)
    /// SHOULD omit the
    /// <see cref="Cvoya.Spring.Core.Units.UnitValidationStep.ValidatingCredential"/>
    /// step from the returned list rather than emitting a no-op; skipping
    /// is cleaner for workflow logs.
    /// </para>
    /// </remarks>
    /// <param name="config">
    /// The tenant's stored install configuration. Implementations typically
    /// read <see cref="AgentRuntimeInstallConfig.DefaultModel"/> to target
    /// the model that the unit's binding will run, and
    /// <see cref="AgentRuntimeInstallConfig.BaseUrl"/> to override the
    /// provider endpoint.
    /// </param>
    /// <param name="credential">
    /// The raw credential to inject into the probe environment. Empty when
    /// <see cref="CredentialSchema"/> is
    /// <see cref="AgentRuntimeCredentialKind.None"/>.
    /// </param>
    /// <returns>
    /// An ordered list of <see cref="ProbeStep"/> values. Never <c>null</c>;
    /// an empty list means the runtime has nothing to probe.
    /// </returns>
    IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential);

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

    /// <summary>
    /// Reports whether the runtime's <paramref name="dispatchPath"/> will
    /// accept the shape of the supplied <paramref name="credential"/>
    /// <b>before</b> any network call. This is the pre-flight format check
    /// consulted by the credential-status probe so the wizard can warn
    /// operators when a stored credential would be rejected by the
    /// dispatch path that will consume it (see #1003).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A return of <c>false</c> means the credential cannot possibly work
    /// on the named path — e.g. a Claude.ai OAuth token dispatched through
    /// the Anthropic Platform REST endpoint (which rejects OAuth tokens
    /// with a 401 indistinguishable from an expired key). A return of
    /// <c>true</c> means the shape is plausible; it does not mean the
    /// credential is authenticated — full validation happens later in the
    /// in-container probe plan or on the first REST call.
    /// </para>
    /// <para>
    /// Empty or whitespace credentials must return <c>true</c> — the
    /// "not configured" state is reported upstream by the resolver; this
    /// check is only concerned with format when a value is present.
    /// </para>
    /// <para>
    /// Runtimes whose <see cref="CredentialSchema"/> is
    /// <see cref="AgentRuntimeCredentialKind.None"/> always return
    /// <c>true</c> — there is no credential format to reject.
    /// </para>
    /// </remarks>
    /// <param name="credential">The raw credential to inspect. May be empty.</param>
    /// <param name="dispatchPath">The dispatch path that will consume the credential.</param>
    /// <returns><c>true</c> when the format is plausible for the path; <c>false</c> when the path is known to reject it.</returns>
    bool IsCredentialFormatAccepted(string credential, CredentialDispatchPath dispatchPath);

    /// <summary>
    /// Probes the runtime's backing service with the supplied
    /// <paramref name="credential"/> and reports whether the credential is
    /// accepted, without persisting any side-effects (e.g. the live model
    /// catalog). Surfaced via
    /// <c>POST /api/v1/agent-runtimes/{id}/validate-credential</c> and the
    /// <c>spring agent-runtime validate-credential</c> CLI verb so operators
    /// can prime / refresh the credential-health row without rotating the
    /// tenant's stored model list (#1066).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default implementation delegates to
    /// <see cref="FetchLiveModelsAsync(string, CancellationToken)"/> and
    /// translates the outcome — runtimes that already speak a cheap REST
    /// round-trip for the catalog get credential validation for free
    /// without re-implementing transport. Override to issue a smaller probe
    /// (for example, an HTTP HEAD or a one-byte completion) when the
    /// catalog endpoint is expensive or the runtime supports cheaper
    /// auth-only probes.
    /// </para>
    /// <para>
    /// Implementations must surface transport-level failures as
    /// <see cref="CredentialValidationStatus.NetworkError"/> rather than
    /// throwing. Authentication failures (401/403 from the backing service)
    /// translate to <see cref="CredentialValidationStatus.Invalid"/>. A
    /// successful round-trip translates to
    /// <see cref="CredentialValidationStatus.Valid"/>.
    /// </para>
    /// <para>
    /// Runtimes whose <see cref="CredentialSchema"/> is
    /// <see cref="AgentRuntimeCredentialKind.None"/> (e.g. local Ollama)
    /// are filtered out by the host endpoint before this method is called,
    /// so implementations do not need to special-case the credential-less
    /// path.
    /// </para>
    /// <para>
    /// The host endpoint that consumes this method records the outcome in
    /// the shared <c>credential_health</c> store so subsequent reads from
    /// <c>spring agent-runtime credentials status</c> (and the portal
    /// banner) reflect the latest probe attempt. This method MUST NOT
    /// touch the tenant's stored model list — refreshing the catalog is
    /// the concern of <see cref="FetchLiveModelsAsync"/> and the
    /// <c>refresh-models</c> path.
    /// </para>
    /// </remarks>
    /// <param name="credential">The raw credential to validate. Empty when the runtime requires no credential.</param>
    /// <param name="cancellationToken">A token to cancel the validation.</param>
    /// <returns>A <see cref="CredentialValidationResult"/> describing the outcome.</returns>
    Task<CredentialValidationResult> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        // Default: piggyback on FetchLiveModelsAsync so a runtime that
        // already implements the live-catalog probe gets a credible
        // credential-validation surface for free. We deliberately do NOT
        // persist the returned model list here — recording the catalog is
        // the host endpoint's concern (and the host's validate-credential
        // endpoint specifically does not write to the install row).
        return ValidateViaFetchLiveModelsAsync(this, credential, cancellationToken);

        static async Task<CredentialValidationResult> ValidateViaFetchLiveModelsAsync(
            IAgentRuntime runtime,
            string credential,
            CancellationToken cancellationToken)
        {
            try
            {
                var fetch = await runtime.FetchLiveModelsAsync(credential, cancellationToken).ConfigureAwait(false);
                return fetch.Status switch
                {
                    FetchLiveModelsStatus.Success => new CredentialValidationResult(
                        Valid: true,
                        ErrorMessage: null,
                        Status: CredentialValidationStatus.Valid,
                        ValidatedAt: DateTimeOffset.UtcNow),
                    FetchLiveModelsStatus.InvalidCredential => new CredentialValidationResult(
                        Valid: false,
                        ErrorMessage: fetch.ErrorMessage,
                        Status: CredentialValidationStatus.Invalid,
                        ValidatedAt: DateTimeOffset.UtcNow),
                    FetchLiveModelsStatus.NetworkError => new CredentialValidationResult(
                        Valid: false,
                        ErrorMessage: fetch.ErrorMessage,
                        Status: CredentialValidationStatus.NetworkError,
                        ValidatedAt: DateTimeOffset.UtcNow),
                    FetchLiveModelsStatus.Unsupported => new CredentialValidationResult(
                        Valid: false,
                        ErrorMessage: fetch.ErrorMessage
                            ?? "Runtime does not support credential validation through the live-catalog endpoint.",
                        Status: CredentialValidationStatus.Unknown,
                        ValidatedAt: DateTimeOffset.UtcNow),
                    _ => new CredentialValidationResult(
                        Valid: false,
                        ErrorMessage: fetch.ErrorMessage ?? "Unknown validation outcome.",
                        Status: CredentialValidationStatus.Unknown,
                        ValidatedAt: DateTimeOffset.UtcNow),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Defensive: any runtime that throws (rather than returning
                // a NetworkError) still produces a clean credential-health
                // signal instead of a 500 from the host endpoint.
                return new CredentialValidationResult(
                    Valid: false,
                    ErrorMessage: ex.Message,
                    Status: CredentialValidationStatus.NetworkError,
                    ValidatedAt: DateTimeOffset.UtcNow);
            }
        }
    }
}