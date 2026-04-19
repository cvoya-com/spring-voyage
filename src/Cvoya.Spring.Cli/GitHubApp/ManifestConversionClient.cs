// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.GitHubApp;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thin HTTP wrapper around GitHub's one-shot
/// <c>POST /app-manifests/{code}/conversions</c> endpoint. The endpoint
/// doesn't require auth — it only accepts the one-time code the browser
/// redirect handed us, and GitHub's own 10-minute TTL protects against
/// replay.
/// </summary>
public sealed class ManifestConversionClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <summary>
    /// Default GitHub base URL. Parameterized purely to keep integration
    /// tests pointed at an in-process mock server.
    /// </summary>
    public const string DefaultGitHubBaseUrl = "https://api.github.com";

    /// <summary>
    /// Creates a new client. The supplied <paramref name="http"/> MUST
    /// have a <c>User-Agent</c> header — GitHub returns <c>403 Forbidden</c>
    /// when that header is absent. The caller owns the HttpClient lifetime.
    /// </summary>
    public ManifestConversionClient(HttpClient http, string baseUrl = DefaultGitHubBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (baseUrl ?? DefaultGitHubBaseUrl).TrimEnd('/');
    }

    /// <summary>
    /// Exchanges a manifest conversion code for the resolved App
    /// credentials. On a non-success response, the raw GitHub error body
    /// is surfaced verbatim so the operator can see whether the failure
    /// is "name taken", "code expired", or something else entirely.
    /// </summary>
    /// <exception cref="ManifestConversionException">
    /// Thrown on any non-2xx response, or when the response body cannot
    /// be deserialized. The exception message contains the raw GitHub
    /// error body.
    /// </exception>
    public async Task<ManifestConversionResult> ExchangeCodeAsync(
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Conversion code is required.", nameof(code));
        }

        var url = $"{_baseUrl}/app-manifests/{Uri.EscapeDataString(code)}/conversions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        // GitHub recommends a version header; the CLI tracks the 2022-11-28
        // API surface. Omitting the header works today but could regress
        // silently when GitHub promotes a newer default.
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ManifestConversionException(
                $"GitHub rejected the conversion (HTTP {(int)response.StatusCode}): {body}",
                (int)response.StatusCode,
                body);
        }

        try
        {
            return JsonSerializer.Deserialize<ManifestConversionResult>(body)
                ?? throw new ManifestConversionException(
                    "GitHub returned an empty JSON body on conversion.",
                    (int)response.StatusCode,
                    body);
        }
        catch (JsonException ex)
        {
            throw new ManifestConversionException(
                $"Could not parse GitHub's conversion response as JSON: {ex.Message}. Body was: {body}",
                (int)response.StatusCode,
                body,
                ex);
        }
    }
}

/// <summary>
/// Raised when the manifest conversion endpoint rejects our code or
/// returns an unreadable body. Exposes the raw HTTP status and body so
/// the CLI can render the message verbatim — the error copy is
/// GitHub's, not ours, and is more actionable than anything we'd
/// paraphrase.
/// </summary>
public sealed class ManifestConversionException : Exception
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public ManifestConversionException(string message, int statusCode, string? responseBody, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}