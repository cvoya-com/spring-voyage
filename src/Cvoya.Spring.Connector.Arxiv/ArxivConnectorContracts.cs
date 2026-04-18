// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv;

/// <summary>
/// Per-unit arxiv connector configuration. Persisted on the unit actor
/// through the generic <see cref="Cvoya.Spring.Connectors.IUnitConnectorConfigStore"/>
/// abstraction.
/// </summary>
/// <param name="DefaultCategories">
/// Default arxiv categories (e.g. <c>cs.AI</c>, <c>cs.LG</c>) to scope searches
/// to when the caller does not specify one. <c>null</c> or empty means no
/// category filter is applied by default.
/// </param>
/// <param name="MaxResults">
/// Default maximum number of results returned by <c>searchLiterature</c> when
/// the caller does not supply a limit. Hard-capped to 100 at call time.
/// </param>
public record UnitArxivConfig(
    IReadOnlyList<string>? DefaultCategories = null,
    int MaxResults = 20);

/// <summary>
/// Request body for
/// <c>PUT /api/v1/connectors/arxiv/units/{unitId}/config</c>. Binds the unit to
/// the arxiv connector and upserts the per-unit config atomically.
/// </summary>
/// <param name="DefaultCategories">Default arxiv categories to scope searches to. Null falls back to no filter.</param>
/// <param name="MaxResults">Default result cap. Null falls back to the connector default (20).</param>
public record UnitArxivConfigRequest(
    IReadOnlyList<string>? DefaultCategories = null,
    int? MaxResults = null);

/// <summary>
/// Response body for
/// <c>GET</c>/<c>PUT /api/v1/connectors/arxiv/units/{unitId}/config</c>.
/// </summary>
/// <param name="UnitId">The unit id this config is bound to.</param>
/// <param name="DefaultCategories">The effective default categories.</param>
/// <param name="MaxResults">The effective default result cap.</param>
public record UnitArxivConfigResponse(
    string UnitId,
    IReadOnlyList<string> DefaultCategories,
    int MaxResults);