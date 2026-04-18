// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fetches the full abstract and metadata for a single arxiv entry by id.
/// Complements <see cref="SearchLiteratureSkill"/> for cases where the agent
/// has an id in hand (e.g. from a citation) and needs the full summary.
/// </summary>
public class FetchAbstractSkill(IArxivClient client, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<FetchAbstractSkill>();

    /// <summary>
    /// Looks the entry up and returns a structured payload, or a
    /// <c>{ found: false }</c> envelope if arxiv reports no match.
    /// </summary>
    /// <param name="arxivId">The arxiv id (e.g. <c>2401.12345</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonElement> ExecuteAsync(
        string arxivId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arxivId);

        _logger.LogInformation("Fetching arxiv abstract for id={ArxivId}", arxivId);

        var entry = await client.GetByIdAsync(arxivId, cancellationToken);
        if (entry is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                found = false,
                id = arxivId,
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            found = true,
            id = entry.Id,
            title = entry.Title,
            @abstract = entry.Summary,
            authors = entry.Authors,
            primary_category = entry.PrimaryCategory,
            categories = entry.Categories,
            published = entry.PublishedUtc,
            updated = entry.UpdatedUtc,
            abs_url = entry.AbsUrl,
            pdf_url = entry.PdfUrl,
            doi = entry.Doi,
        });
    }
}