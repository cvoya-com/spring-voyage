// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch;

/// <summary>
/// Per-unit web-search connector configuration. Picks the provider
/// implementation to route searches through and carries the name of the
/// unit-scoped secret holding that provider's API key.
/// </summary>
/// <param name="Provider">
/// The provider id (e.g. <c>brave</c>). Must match the <c>Id</c> of a
/// registered <see cref="IWebSearchProvider"/>; unknown providers are
/// rejected at bind time.
/// </param>
/// <param name="ApiKeySecretName">
/// The name of a unit-scoped secret (see <see cref="Cvoya.Spring.Core.Secrets.SecretScope.Unit"/>)
/// whose value is the provider's API key. Resolved at invoke time via
/// <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/>; never persisted
/// inline here so the stored binding is free of plaintext. <c>null</c> is
/// allowed for providers that do not require authentication (e.g. a
/// self-hosted SearxNG instance).
/// </param>
/// <param name="MaxResults">Default cap on the number of results the skill returns. Hard-capped to 50.</param>
/// <param name="Safesearch">Whether the provider should enable its safe-search filter.</param>
public record UnitWebSearchConfig(
    string Provider,
    string? ApiKeySecretName,
    int MaxResults = 10,
    bool Safesearch = true);

/// <summary>
/// Request body for
/// <c>PUT /api/v1/connectors/web-search/units/{unitId}/config</c>. Mirrors
/// <see cref="UnitWebSearchConfig"/> but makes every non-essential field
/// nullable so callers can accept the defaults.
/// </summary>
/// <param name="Provider">The provider id. Required.</param>
/// <param name="ApiKeySecretName">Unit-scoped secret name for the API key. Null means the provider does not need one.</param>
/// <param name="MaxResults">Default result cap. Null falls back to the connector default (10).</param>
/// <param name="Safesearch">Whether to enable safe-search. Null falls back to true.</param>
public record UnitWebSearchConfigRequest(
    string Provider,
    string? ApiKeySecretName = null,
    int? MaxResults = null,
    bool? Safesearch = null);

/// <summary>
/// Response body for
/// <c>GET</c>/<c>PUT /api/v1/connectors/web-search/units/{unitId}/config</c>.
/// Never contains a plaintext API key — only the secret name reference.
/// </summary>
/// <param name="UnitId">The unit id this config is bound to.</param>
/// <param name="Provider">The selected provider id.</param>
/// <param name="ApiKeySecretName">The unit-scoped secret name for the API key.</param>
/// <param name="MaxResults">The effective result cap.</param>
/// <param name="Safesearch">The effective safe-search flag.</param>
public record UnitWebSearchConfigResponse(
    string UnitId,
    string Provider,
    string? ApiKeySecretName,
    int MaxResults,
    bool Safesearch);

/// <summary>
/// Response item for
/// <c>GET /api/v1/connectors/web-search/actions/providers</c>. Lists every
/// <see cref="IWebSearchProvider"/> currently registered in the host so the
/// portal / CLI can render the provider picker without hard-coding the list.
/// </summary>
/// <param name="Id">The provider id.</param>
/// <param name="DisplayName">The provider's human-facing display name.</param>
public record WebSearchProviderDescriptor(string Id, string DisplayName);
