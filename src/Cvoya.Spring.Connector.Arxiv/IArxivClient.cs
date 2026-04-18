// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv;

/// <summary>
/// Abstraction over the arxiv <c>export.arxiv.org/api/query</c> surface so the
/// skill layer stays testable. Implementations issue HTTP requests against the
/// public arxiv endpoint and parse the Atom response into a typed model. This
/// is a read-only surface — arxiv does not publish a write API and the
/// connector deliberately offers no write actions.
/// </summary>
public interface IArxivClient
{
    /// <summary>
    /// Searches the arxiv catalogue.
    /// </summary>
    /// <param name="query">Free-form query string — passed through to the arxiv <c>search_query</c> parameter.</param>
    /// <param name="categories">Optional arxiv category filters (e.g. <c>cs.AI</c>). AND-joined into the query.</param>
    /// <param name="yearFrom">Optional inclusive lower bound on publication year.</param>
    /// <param name="yearTo">Optional inclusive upper bound on publication year.</param>
    /// <param name="maxResults">Maximum number of entries to return. Capped at 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ArxivEntry>> SearchAsync(
        string query,
        IReadOnlyList<string>? categories,
        int? yearFrom,
        int? yearTo,
        int maxResults,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a single arxiv entry by its id (e.g. <c>2401.12345</c> or
    /// <c>cs/0001001</c>). Returns <c>null</c> when arxiv returns no match.
    /// </summary>
    /// <param name="id">The arxiv identifier without the <c>arXiv:</c> prefix and without any version suffix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ArxivEntry?> GetByIdAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// Typed arxiv entry projection. Only the fields the skill layer surfaces are
/// populated; everything else is dropped to keep the payload size small.
/// </summary>
/// <param name="Id">The canonical arxiv id (e.g. <c>2401.12345</c>).</param>
/// <param name="Title">The entry title.</param>
/// <param name="Summary">The abstract.</param>
/// <param name="Authors">Author names, in the order arxiv returned them.</param>
/// <param name="PublishedUtc">The publication timestamp in UTC.</param>
/// <param name="UpdatedUtc">The last-updated timestamp in UTC.</param>
/// <param name="PrimaryCategory">The primary arxiv category (e.g. <c>cs.AI</c>).</param>
/// <param name="Categories">All category labels on the entry.</param>
/// <param name="PdfUrl">The canonical PDF URL.</param>
/// <param name="AbsUrl">The canonical abs (landing page) URL.</param>
/// <param name="Doi">The associated DOI, if arxiv reported one.</param>
public record ArxivEntry(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<string> Authors,
    DateTimeOffset PublishedUtc,
    DateTimeOffset UpdatedUtc,
    string? PrimaryCategory,
    IReadOnlyList<string> Categories,
    string PdfUrl,
    string AbsUrl,
    string? Doi);