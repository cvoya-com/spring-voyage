// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Response body for <c>GET /api/v1/agent-runtimes</c> and the
/// install-management endpoints. Combines the runtime's type-descriptor
/// fields (sourced from the registry) with the tenant install metadata
/// (from <c>tenant_agent_runtime_installs</c>).
/// </summary>
/// <param name="Id">
/// Stable runtime identifier (matches <c>IAgentRuntime.Id</c>). Persisted
/// with every unit binding so a display-name change never breaks stored
/// data.
/// </param>
/// <param name="DisplayName">Human-facing display name from the runtime descriptor.</param>
/// <param name="ToolKind">
/// Execution tool the runtime uses (e.g. <c>claude-code-cli</c>,
/// <c>dapr-agent</c>). Lets clients reason about container baseline
/// requirements without importing runtime packages.
/// </param>
/// <param name="InstalledAt">Timestamp when the runtime was first installed on the tenant.</param>
/// <param name="UpdatedAt">Timestamp when the install row was last updated.</param>
/// <param name="Models">
/// Model ids the tenant has enabled for this runtime. Empty when the
/// tenant inherits the runtime's seed defaults (the wizard should then
/// fall back to calling <c>GET /api/v1/agent-runtimes/{id}/models</c>).
/// </param>
/// <param name="DefaultModel">
/// Preferred model id — used by the wizard to pre-select a value. <c>null</c>
/// when the tenant has not pinned one.
/// </param>
/// <param name="BaseUrl">
/// Optional base URL override used by runtimes that support self-hosted
/// or proxied endpoints.
/// </param>
/// <param name="CredentialKind">
/// The kind of credential the runtime expects at accept-time. Drives
/// whether the wizard renders a credential input at all
/// (<see cref="AgentRuntimeCredentialKind.None"/> = skip).
/// </param>
/// <param name="CredentialDisplayHint">
/// Optional human-facing hint describing the credential format or how to
/// obtain it. Surfaces next to the wizard input. <c>null</c> when the
/// runtime declares no hint.
/// </param>
/// <param name="CredentialSecretName">
/// Canonical secret name under which the runtime's credential is stored
/// (e.g. <c>anthropic-api-key</c>, <c>openai-api-key</c>,
/// <c>google-api-key</c>). Mirrors <c>IAgentRuntime.CredentialSecretName</c>
/// so the CLI wizard — which runs client-side without DI access to the
/// runtime registry — can resolve the secret name by reading this field
/// instead of hardcoding the mapping. Empty string when the runtime
/// declares no credential (for example, Ollama): callers MUST treat the
/// empty case as "no credential to write" and skip any secret write.
/// </param>
public record InstalledAgentRuntimeResponse(
    string Id,
    string DisplayName,
    string ToolKind,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Models,
    string? DefaultModel,
    string? BaseUrl,
    AgentRuntimeCredentialKind CredentialKind,
    string? CredentialDisplayHint,
    string CredentialSecretName);

/// <summary>
/// Single entry in the response to <c>GET /api/v1/agent-runtimes/{id}/models</c>.
/// </summary>
/// <param name="Id">Stable model id used by the backing service.</param>
/// <param name="DisplayName">Human-facing label for the model.</param>
/// <param name="ContextWindow">Context window in tokens, if known; <c>null</c> otherwise.</param>
public record AgentRuntimeModelResponse(
    string Id,
    string DisplayName,
    int? ContextWindow);

/// <summary>
/// Request body for <c>POST /api/v1/agent-runtimes/{id}/install</c>. When
/// every field is null the service materialises the config from the
/// runtime's seed defaults.
/// </summary>
/// <param name="Models">Override model list, or <c>null</c> to inherit the runtime's seed defaults.</param>
/// <param name="DefaultModel">Override default model, or <c>null</c> to pick the first of <paramref name="Models"/>.</param>
/// <param name="BaseUrl">Optional base URL override.</param>
public record AgentRuntimeInstallRequest(
    IReadOnlyList<string>? Models,
    string? DefaultModel,
    string? BaseUrl);

/// <summary>
/// Request body for <c>POST /api/v1/agent-runtimes/{id}/refresh-models</c>.
/// The endpoint invokes the runtime's
/// <c>FetchLiveModelsAsync</c> with the supplied credential and, on
/// success, replaces the tenant's configured model list with the returned
/// catalog.
/// </summary>
/// <param name="Credential">
/// Raw credential presented to the backing service to authorise the live
/// catalog lookup. Runtimes that require no credential (e.g. local
/// Ollama) ignore this field.
/// </param>
public record AgentRuntimeRefreshModelsRequest(
    string? Credential);