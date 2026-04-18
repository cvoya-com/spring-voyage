// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

/// <summary>
/// Executes a literature search against the arxiv catalogue. Surface mirrors
/// the <c>searchLiterature</c> tool declared by the research package's
/// <c>literature-review</c> skill bundle, so binding this connector to a
/// research unit self-resolves the bundle's "referenced tool not present"
/// warning.
/// </summary>
public class SearchLiteratureSkill(IArxivClient client, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<SearchLiteratureSkill>();

    /// <summary>
    /// Runs the search and returns the projected matches.
    /// </summary>
    /// <param name="query">Free-form query string.</param>
    /// <param name="categories">Optional arxiv categories to scope the search to.</param>
    /// <param name="yearFrom">Optional inclusive lower bound on publication year.</param>
    /// <param name="yearTo">Optional inclusive upper bound on publication year.</param>
    /// <param name="limit">Maximum number of results. Capped at 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonElement> ExecuteAsync(
        string query,
        IReadOnlyList<string>? categories,
        int? yearFrom,
        int? yearTo,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        _logger.LogInformation(
            "Searching arxiv query='{Query}' cats={CatCount} limit={Limit}",
            query, categories?.Count ?? 0, limit);

        var entries = await client.SearchAsync(
            query, categories, yearFrom, yearTo, limit, cancellationToken);

        var results = entries.Select(e => new
        {
            id = e.Id,
            title = e.Title,
            abstract_excerpt = Excerpt(e.Summary, 320),
            authors = e.Authors,
            primary_category = e.PrimaryCategory,
            categories = e.Categories,
            published = e.PublishedUtc,
            updated = e.UpdatedUtc,
            abs_url = e.AbsUrl,
            pdf_url = e.PdfUrl,
            doi = e.Doi,
            source_type = "preprint",
        }).ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            results,
            count = results.Length,
        });
    }

    private static string Excerpt(string body, int maxChars)
    {
        if (string.IsNullOrEmpty(body) || body.Length <= maxChars)
        {
            return body;
        }
        return body[..maxChars] + "...";
    }
}