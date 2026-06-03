// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.ModelProviders;

/// <summary>
/// Response body for <c>GET /api/v1/tenant/model-providers/installs</c>
/// and the install-management endpoints under ADR-0038. Combines the
/// model-provider's catalogue fields with the tenant install metadata
/// (from <c>tenant_agent_runtime_installs</c>, re-keyed on provider id
/// per ADR-0038 § "Provider is the credential and routing boundary").
/// </summary>
/// <remarks>
/// Example:
/// <code>
/// {
///   "id": "anthropic",
///   "displayName": "Anthropic",
///   "installedAt": "2026-05-06T10:00:00Z",
///   "updatedAt": "2026-05-06T10:00:00Z",
///   "models": ["claude-opus-4-8", "claude-sonnet-4-6"],
///   "defaultModel": "claude-opus-4-8",
///   "baseUrl": null,
///   "credentialKind": "ApiKey",
///   "credentialDisplayHint": null,
///   "credentialSecretName": "anthropic-api-key"
/// }
/// </code>
/// </remarks>
/// <param name="Id">
/// Stable provider id (matches an entry in
/// <c>eng/runtime-catalog/runtime-catalog.yaml</c> § <c>modelProviders</c>):
/// <c>anthropic</c>, <c>openai</c>, <c>google</c>, <c>ollama</c>, …
/// </param>
/// <param name="DisplayName">Human-facing display name from the catalogue.</param>
/// <param name="InstalledAt">When the provider was first installed on the tenant.</param>
/// <param name="UpdatedAt">When the install row was last updated.</param>
/// <param name="Models">Model ids the tenant has enabled for this provider.</param>
/// <param name="DefaultModel">Pinned default model id, or <c>null</c>.</param>
/// <param name="BaseUrl">Optional base-URL override.</param>
/// <param name="CredentialKind">
/// The kind of credential this provider expects. Drives whether the
/// wizard renders a credential input at all
/// (<see cref="ModelProviderCredentialKind.None"/> = skip).
/// </param>
/// <param name="CredentialDisplayHint">Human-facing hint for the credential input.</param>
/// <param name="CredentialSecretName">
/// Canonical secret name under which the provider's credential is stored
/// (e.g. <c>anthropic-api-key</c>). Empty when the provider requires no
/// credential (e.g. local Ollama).
/// </param>
public record InstalledModelProviderResponse(
    string Id,
    string DisplayName,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Models,
    string? DefaultModel,
    string? BaseUrl,
    ModelProviderCredentialKind CredentialKind,
    string? CredentialDisplayHint,
    string CredentialSecretName);

/// <summary>
/// Single entry in the response to
/// <c>GET /api/v1/tenant/model-providers/installs/{id}/models</c>.
/// </summary>
/// <param name="Id">Stable model id used by the backing service.</param>
/// <param name="DisplayName">Human-facing label for the model.</param>
/// <param name="ContextWindow">Context window in tokens, if known; <c>null</c> otherwise.</param>
public record ModelProviderModelResponse(
    string Id,
    string DisplayName,
    int? ContextWindow);

/// <summary>
/// Request body for <c>POST /api/v1/tenant/model-providers/installs/{id}/install</c>
/// (ADR-0038). When every field is null the service materialises the
/// config from the provider's catalogue defaults
/// (<c>modelProviders[].defaultModels</c>).
/// </summary>
/// <param name="Models">Override model list, or <c>null</c> to inherit catalogue defaults.</param>
/// <param name="DefaultModel">Override default model, or <c>null</c> to pick the first of <paramref name="Models"/>.</param>
/// <param name="BaseUrl">Optional base URL override.</param>
public record ModelProviderInstallRequest(
    IReadOnlyList<string>? Models,
    string? DefaultModel,
    string? BaseUrl);

/// <summary>
/// Request body for
/// <c>POST /api/v1/tenant/model-providers/installs/{id}/refresh-models</c>
/// (ADR-0038). The endpoint invokes the provider adapter's
/// <c>FetchLiveModelsAsync</c> with the supplied credential and, on
/// success, replaces the tenant's configured model list with the
/// returned catalogue.
/// </summary>
/// <param name="Credential">
/// Raw credential presented to the backing service to authorise the
/// live catalogue lookup. Providers that require no credential (e.g.
/// local Ollama) ignore this field.
/// </param>
public record ModelProviderRefreshModelsRequest(
    string? Credential);

/// <summary>
/// Response body for
/// <c>GET /api/v1/tenant/model-providers/installs/{id}/config</c> — the
/// tenant-scoped configuration slot for an installed provider, in
/// isolation from the rest of the install metadata.
/// </summary>
/// <param name="Id">Stable provider id.</param>
/// <param name="Models">Tenant-configured model id list (may be empty when inheriting the seed).</param>
/// <param name="DefaultModel">Pinned default model id, or <c>null</c>.</param>
/// <param name="BaseUrl">Optional base URL override, or <c>null</c>.</param>
public record ModelProviderConfigResponse(
    string Id,
    IReadOnlyList<string> Models,
    string? DefaultModel,
    string? BaseUrl);

/// <summary>
/// Request body for
/// <c>POST /api/v1/tenant/model-providers/installs/{id}/validate-credential</c>.
/// The endpoint invokes the provider adapter's credential probe with the
/// supplied credential, records the outcome in the credential-health
/// store, and returns the result. It does NOT touch the tenant's
/// configured model list — refreshing the catalogue is the
/// responsibility of <c>refresh-models</c>.
/// </summary>
/// <param name="Credential">
/// Raw credential to probe with. Providers that require no credential
/// (e.g. local Ollama) ignore this field; the endpoint short-circuits
/// to a friendly "no credential required" payload for those.
/// </param>
/// <param name="SecretName">
/// Optional secret-name slot for the credential-health row. Defaults to
/// <c>"default"</c>. Multi-credential providers supply a stable name so
/// each row updates independently.
/// </param>
public record ModelProviderValidateCredentialRequest(
    string? Credential,
    string? SecretName);

/// <summary>
/// Response body for
/// <c>POST /api/v1/tenant/model-providers/installs/{id}/validate-credential</c>.
/// </summary>
/// <param name="Ok"><c>true</c> when the provider accepted the credential.</param>
/// <param name="Status">Persistent status recorded in the credential-health store.</param>
/// <param name="Detail">Human-readable explanation when <paramref name="Ok"/> is <c>false</c>.</param>
/// <param name="ValidatedAt">Wall-clock timestamp of the probe attempt.</param>
public record ModelProviderValidateCredentialResponse(
    bool Ok,
    Cvoya.Spring.Core.CredentialHealth.CredentialHealthStatus Status,
    string? Detail,
    DateTimeOffset ValidatedAt);
