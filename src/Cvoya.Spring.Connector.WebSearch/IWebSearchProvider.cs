// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch;

/// <summary>
/// Abstraction the web-search connector sits behind so different underlying
/// search APIs (Brave, Bing, Google Custom Search, SearxNG, ...) can be
/// slotted in without changing the connector, its skills, or its config
/// surface. Implementations are keyed by <see cref="Id"/> — the
/// <c>provider</c> field on <see cref="UnitWebSearchConfig"/> selects which
/// registered implementation handles a given unit's searches.
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// The stable, lowercase identifier used to select this provider from the
    /// per-unit config (e.g. <c>brave</c>, <c>bing</c>, <c>google</c>). Must be
    /// unique across all registered providers in a host.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-facing display name (e.g. <c>Brave Search</c>). Surfaced in the
    /// config-schema enum description so operators know what they are picking.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Executes a search against the provider.
    /// </summary>
    /// <param name="request">The normalised search request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider's results, already capped to <see cref="WebSearchRequest.Limit"/>.</returns>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        WebSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Inputs to a single provider search call. <see cref="ApiKey"/> is resolved
/// by the connector layer through <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/>
/// and passed in plaintext to the provider; providers MUST treat this as
/// sensitive and never include it in logs.
/// </summary>
/// <param name="Query">The search query.</param>
/// <param name="Limit">Maximum number of results requested. Capped to 50.</param>
/// <param name="Safesearch">Whether the provider should enable its safe-search filter.</param>
/// <param name="ApiKey">Resolved plaintext API key, or <c>null</c> when the provider does not require one.</param>
public record WebSearchRequest(
    string Query,
    int Limit,
    bool Safesearch,
    string? ApiKey);

/// <summary>
/// Provider-agnostic search-result shape. Provider implementations normalise
/// their native responses into this.
/// </summary>
/// <param name="Title">The result title.</param>
/// <param name="Url">The result URL.</param>
/// <param name="Snippet">A short provider-supplied excerpt or summary.</param>
/// <param name="Source">Optional source label (site name or publisher).</param>
public record WebSearchResult(
    string Title,
    string Url,
    string Snippet,
    string? Source);
