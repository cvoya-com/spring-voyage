// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch;

using Cvoya.Spring.Connector.WebSearch.Providers;

/// <summary>
/// Bound from the <c>WebSearch</c> configuration section in
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddCvoyaSpringConnectorWebSearch"/>.
/// </summary>
public class WebSearchConnectorOptions
{
    /// <summary>
    /// The provider id used when a unit is bound without an explicit
    /// provider selection. Defaults to <see cref="BraveSearchProvider.ProviderId"/>
    /// — see the connector README for why Brave was picked as the OSS default.
    /// </summary>
    public string DefaultProvider { get; set; } = BraveSearchProvider.ProviderId;

    /// <summary>
    /// Default per-unit result cap when the caller doesn't specify one. Hard
    /// capped at 50 by the skill layer.
    /// </summary>
    public int DefaultMaxResults { get; set; } = 10;

    /// <summary>
    /// Per-provider settings — currently just endpoint overrides for tests.
    /// </summary>
    public BraveProviderOptions Brave { get; set; } = new();
}

/// <summary>
/// Brave-specific knobs. Overridable from configuration so integration tests
/// can point at a mock.
/// </summary>
public class BraveProviderOptions
{
    /// <summary>
    /// The base URL of the Brave Search API. Defaults to the public endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.search.brave.com/res/v1";
}
