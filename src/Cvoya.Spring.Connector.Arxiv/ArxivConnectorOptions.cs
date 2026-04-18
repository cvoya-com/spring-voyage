// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv;

/// <summary>
/// Bound from the <c>Arxiv</c> configuration section in
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddCvoyaSpringConnectorArxiv"/>.
/// arxiv's public API requires no authentication, so the options surface is
/// limited to the endpoint override for integration tests and the default
/// per-unit max-results cap.
/// </summary>
public class ArxivConnectorOptions
{
    /// <summary>
    /// The base URL of the arxiv query API. Defaults to the public
    /// <c>https://export.arxiv.org/api</c> endpoint; overridable so integration
    /// tests can point at a mock.
    /// </summary>
    public string BaseUrl { get; set; } = "https://export.arxiv.org/api";

    /// <summary>
    /// Default max-results cap used when a unit is bound to the connector
    /// without providing its own override. Hard-capped to 100 by the skill
    /// layer regardless of this value.
    /// </summary>
    public int DefaultMaxResults { get; set; } = 20;
}