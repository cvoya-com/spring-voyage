// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.Providers;

using System.Net.Http;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IWebSearchProvider"/> implementation backed by the Brave Search
/// API (<c>api.search.brave.com</c>). Picked as the OSS default because
/// Brave's Data-for-Search tier has a free plan, a single HTTPS endpoint, an
/// API-key-header auth model (no OAuth dance), and a broad web index — the
/// simplest slot-in for a default without requiring a Google Cloud project or
/// an Azure subscription up front.
///
/// Additional providers (Bing, Google Custom Search, SearxNG, ...) can be
/// registered alongside this one by implementing <see cref="IWebSearchProvider"/>
/// and adding them to DI; the connector picks whichever one the unit's config
/// selects through its <c>provider</c> field.
/// </summary>
internal sealed class BraveSearchProvider : IWebSearchProvider
{
    /// <summary>The provider id used in <see cref="UnitWebSearchConfig.Provider"/>.</summary>
    public const string ProviderId = "brave";

    /// <summary>The named <see cref="HttpClient"/> used for outbound calls.</summary>
    public const string HttpClientName = "web-search-brave";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebSearchConnectorOptions _options;
    private readonly ILogger<BraveSearchProvider> _logger;

    public BraveSearchProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<WebSearchConnectorOptions> options,
        ILogger<BraveSearchProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "Brave Search";

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        WebSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        if (string.IsNullOrEmpty(request.ApiKey))
        {
            // Log without the key. "Missing" is already a non-sensitive fact;
            // we never log anything that could leak the secret value.
            _logger.LogWarning(
                "Brave search provider was invoked without an API key. Configure a unit-scoped secret and reference it via 'apiKeySecretName'.");
            throw new InvalidOperationException(
                "Brave Search requires an API key. Configure a unit-scoped secret and reference it via the connector's 'apiKeySecretName' field.");
        }

        var cap = Math.Clamp(request.Limit, 1, 50);
        var url = $"{_options.Brave.BaseUrl.TrimEnd('/')}/web/search"
            + $"?q={Uri.EscapeDataString(request.Query)}"
            + $"&count={cap}"
            + $"&safesearch={(request.Safesearch ? "strict" : "off")}";

        _logger.LogInformation("Issuing Brave search for query={QueryLength}chars limit={Limit}", request.Query.Length, cap);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        // Brave expects the token in the X-Subscription-Token header. We only
        // attach it to this request scope — never on the cached default client.
        message.Headers.Add("X-Subscription-Token", request.ApiKey);
        message.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ParseResults(doc);
    }

    internal static IReadOnlyList<WebSearchResult> ParseResults(JsonDocument doc)
    {
        var results = new List<WebSearchResult>();
        if (!doc.RootElement.TryGetProperty("web", out var web)
            || !web.TryGetProperty("results", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in items.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var snippet = item.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            string? source = null;
            if (item.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                source = name.GetString();
            }

            if (!string.IsNullOrEmpty(url))
            {
                results.Add(new WebSearchResult(title, url, snippet, source));
            }
        }

        return results;
    }
}
