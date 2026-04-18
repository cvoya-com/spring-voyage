// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv;

using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IArxivClient"/> implementation backed by the public
/// arxiv Atom export at <c>https://export.arxiv.org/api/query</c>. The arxiv
/// API does not require authentication; there are no secrets to redact here,
/// so the client logs the full outbound URL to aid debugging.
/// </summary>
internal sealed class ArxivClient : IArxivClient
{
    /// <summary>The named <see cref="HttpClient"/> used for outbound calls.</summary>
    public const string HttpClientName = "arxiv";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace ArxivNs = "http://arxiv.org/schemas/atom";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArxivConnectorOptions _options;
    private readonly ILogger<ArxivClient> _logger;

    public ArxivClient(
        IHttpClientFactory httpClientFactory,
        IOptions<ArxivConnectorOptions> options,
        ILogger<ArxivClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArxivEntry>> SearchAsync(
        string query,
        IReadOnlyList<string>? categories,
        int? yearFrom,
        int? yearTo,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var searchQuery = BuildSearchQuery(query, categories, yearFrom, yearTo);
        var cap = Math.Clamp(maxResults, 1, 100);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/query?search_query={Uri.EscapeDataString(searchQuery)}&start=0&max_results={cap}&sortBy=relevance&sortOrder=descending";
        return await FetchAsync(url, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ArxivEntry?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/query?id_list={Uri.EscapeDataString(id)}&max_results=1";
        var entries = await FetchAsync(url, cancellationToken);
        return entries.Count == 0 ? null : entries[0];
    }

    private async Task<IReadOnlyList<ArxivEntry>> FetchAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching arxiv feed {Url}", url);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        return ParseFeed(doc);
    }

    internal static IReadOnlyList<ArxivEntry> ParseFeed(XDocument doc)
    {
        var entries = new List<ArxivEntry>();
        if (doc.Root is null)
        {
            return entries;
        }

        foreach (var entryElement in doc.Root.Elements(Atom + "entry"))
        {
            entries.Add(ParseEntry(entryElement));
        }

        return entries;
    }

    private static ArxivEntry ParseEntry(XElement entry)
    {
        var idUrl = (string?)entry.Element(Atom + "id") ?? string.Empty;
        var id = CanonicaliseId(idUrl);
        var title = NormaliseWhitespace((string?)entry.Element(Atom + "title") ?? string.Empty);
        var summary = NormaliseWhitespace((string?)entry.Element(Atom + "summary") ?? string.Empty);

        var authors = entry.Elements(Atom + "author")
            .Select(a => NormaliseWhitespace((string?)a.Element(Atom + "name") ?? string.Empty))
            .Where(n => n.Length > 0)
            .ToArray();

        var published = ParseDate(entry.Element(Atom + "published"));
        var updated = ParseDate(entry.Element(Atom + "updated"));

        var primaryCategory = (string?)entry.Element(ArxivNs + "primary_category")?.Attribute("term");
        var categories = entry.Elements(Atom + "category")
            .Select(c => (string?)c.Attribute("term"))
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .ToArray();

        var pdfUrl = entry.Elements(Atom + "link")
            .FirstOrDefault(l => (string?)l.Attribute("title") == "pdf")
            ?.Attribute("href")?.Value
            ?? BuildDerivedPdfUrl(id);
        var absUrl = entry.Elements(Atom + "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") == "alternate")
            ?.Attribute("href")?.Value
            ?? idUrl;
        var doi = (string?)entry.Element(ArxivNs + "doi");

        return new ArxivEntry(
            Id: id,
            Title: title,
            Summary: summary,
            Authors: authors,
            PublishedUtc: published,
            UpdatedUtc: updated,
            PrimaryCategory: primaryCategory,
            Categories: categories,
            PdfUrl: pdfUrl,
            AbsUrl: absUrl,
            Doi: doi);
    }

    private static string CanonicaliseId(string idUrl)
    {
        // arxiv ids are returned as e.g. "http://arxiv.org/abs/2401.12345v2".
        // Strip the host prefix and any version suffix so callers get a stable
        // key matching what they would pass into GetByIdAsync.
        var idx = idUrl.LastIndexOf("/abs/", StringComparison.Ordinal);
        if (idx >= 0)
        {
            idUrl = idUrl[(idx + "/abs/".Length)..];
        }
        var v = idUrl.LastIndexOf('v');
        if (v > 0 && int.TryParse(idUrl[(v + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            idUrl = idUrl[..v];
        }
        return idUrl;
    }

    private static string BuildDerivedPdfUrl(string id)
        => $"https://arxiv.org/pdf/{id}";

    private static DateTimeOffset ParseDate(XElement? element)
    {
        if (element is null || string.IsNullOrWhiteSpace(element.Value))
        {
            return default;
        }
        return DateTimeOffset.TryParse(
            element.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : default;
    }

    private static string NormaliseWhitespace(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(input.Length);
        var lastWasSpace = false;
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    internal static string BuildSearchQuery(
        string query,
        IReadOnlyList<string>? categories,
        int? yearFrom,
        int? yearTo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var parts = new List<string>(4)
        {
            $"all:{query}",
        };

        if (categories is { Count: > 0 })
        {
            var cats = string.Join("+OR+", categories.Select(c => $"cat:{c}"));
            parts.Add($"({cats})");
        }

        if (yearFrom is { } from || yearTo is { } to)
        {
            // arxiv date filters operate over submittedDate in YYYYMMDDHHMM form.
            var startYear = yearFrom ?? 1900;
            var endYear = yearTo ?? DateTime.UtcNow.Year;
            parts.Add($"submittedDate:[{startYear}01010000 TO {endYear}12312359]");
        }

        return string.Join(" AND ", parts);
    }
}