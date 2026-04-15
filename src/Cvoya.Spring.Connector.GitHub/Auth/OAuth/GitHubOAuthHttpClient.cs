// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IGitHubOAuthHttpClient"/>. Uses a
/// <see cref="HttpClient"/> obtained from
/// <see cref="IHttpClientFactory"/> under the named client
/// <c>"github-oauth"</c>. The host can post-configure that named client
/// with proxies / handlers as needed; the HTTPS scheme is enforced at
/// this class level as an independent safety net.
/// </summary>
public class GitHubOAuthHttpClient : IGitHubOAuthHttpClient
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> this class resolves through
    /// <see cref="IHttpClientFactory"/>. Exposed as a constant so tests
    /// and host configuration can target the same logical client.
    /// </summary>
    public const string HttpClientName = "github-oauth";

    internal const string AccessTokenEndpoint = "https://github.com/login/oauth/access_token";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a new OAuth HTTP client.
    /// </summary>
    public GitHubOAuthHttpClient(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<GitHubOAuthHttpClient>();
    }

    /// <inheritdoc />
    public async Task<OAuthTokenExchangeResult> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        CancellationToken ct)
    {
        EnsureHttps(AccessTokenEndpoint);

        using var http = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SpringVoyage-GitHubConnector");

        // URL-form body — GitHub accepts either JSON or form; form is the
        // canonical shape in the docs and avoids a JSON serialiser dependency
        // just to POST four fields.
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("redirect_uri", redirectUri),
        });
        request.Content = form;

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // GitHub sometimes returns 200 with an error body and sometimes
            // returns a non-2xx; we treat the non-2xx path as a generic
            // upstream failure. The client secret is NEVER logged — we
            // include only the response status and the client id prefix.
            _logger.LogWarning(
                "OAuth code exchange failed with status {StatusCode} for client id prefix {ClientIdPrefix}",
                response.StatusCode,
                Prefix(clientId));
            return new OAuthTokenExchangeResult(
                AccessToken: null,
                RefreshToken: null,
                ExpiresAt: null,
                GrantedScopes: string.Empty,
                Error: $"http_{(int)response.StatusCode}",
                ErrorDescription: response.ReasonPhrase);
        }

        return Parse(body);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeTokenAsync(
        string clientId,
        string clientSecret,
        string accessToken,
        CancellationToken ct)
    {
        var endpoint = $"https://api.github.com/applications/{Uri.EscapeDataString(clientId)}/grant";
        EnsureHttps(endpoint);

        using var http = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.UserAgent.ParseAdd("SpringVoyage-GitHubConnector");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // The grant-revocation API uses HTTP Basic auth with client_id:client_secret.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        // Payload carries the access token to revoke.
        var payload = JsonSerializer.Serialize(new { access_token = accessToken });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        _logger.LogWarning(
            "OAuth token revocation returned status {StatusCode} for client id prefix {ClientIdPrefix}",
            response.StatusCode, Prefix(clientId));
        return false;
    }

    /// <summary>
    /// Parses a token-exchange response body into the
    /// <see cref="OAuthTokenExchangeResult"/> shape. Public so tests can
    /// assert the parser independent of the HTTP transport.
    /// </summary>
    public OAuthTokenExchangeResult Parse(string body)
    {
        // GitHub returns either JSON (when we ask for it via Accept) or
        // URL-encoded form. We asked for JSON but be defensive.
        if (body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            {
                var errDesc = root.TryGetProperty("error_description", out var d)
                    && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null;
                return new OAuthTokenExchangeResult(
                    AccessToken: null,
                    RefreshToken: null,
                    ExpiresAt: null,
                    GrantedScopes: string.Empty,
                    Error: errEl.GetString(),
                    ErrorDescription: errDesc);
            }

            var accessToken = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString()
                : null;
            var refreshToken = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()
                : null;
            var scope = root.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String
                ? sc.GetString() ?? string.Empty
                : string.Empty;
            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number)
            {
                expiresAt = _timeProvider.GetUtcNow().AddSeconds(ei.GetInt32());
            }

            return new OAuthTokenExchangeResult(
                AccessToken: accessToken,
                RefreshToken: refreshToken,
                ExpiresAt: expiresAt,
                GrantedScopes: scope,
                Error: accessToken is null ? "missing_access_token" : null,
                ErrorDescription: accessToken is null ? "GitHub response did not include an access_token field." : null);
        }

        // URL-form fallback. Kept small — the JSON path is the expected one.
        var parsed = ParseUrlEncodedBody(body);
        parsed.TryGetValue("access_token", out var token);
        parsed.TryGetValue("error", out var errForm);
        if (!string.IsNullOrEmpty(errForm))
        {
            parsed.TryGetValue("error_description", out var errDescription);
            return new OAuthTokenExchangeResult(
                AccessToken: null,
                RefreshToken: null,
                ExpiresAt: null,
                GrantedScopes: string.Empty,
                Error: errForm,
                ErrorDescription: errDescription);
        }

        DateTimeOffset? expiresAtForm = null;
        if (parsed.TryGetValue("expires_in", out var expiresInRaw)
            && int.TryParse(expiresInRaw, out var seconds))
        {
            expiresAtForm = _timeProvider.GetUtcNow().AddSeconds(seconds);
        }

        parsed.TryGetValue("refresh_token", out var refreshTokenForm);
        parsed.TryGetValue("scope", out var scopeForm);

        return new OAuthTokenExchangeResult(
            AccessToken: token,
            RefreshToken: refreshTokenForm,
            ExpiresAt: expiresAtForm,
            GrantedScopes: scopeForm ?? string.Empty,
            Error: string.IsNullOrEmpty(token) ? "missing_access_token" : null,
            ErrorDescription: string.IsNullOrEmpty(token) ? "GitHub response did not include an access_token field." : null);
    }

    private static Dictionary<string, string> ParseUrlEncodedBody(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body))
        {
            return result;
        }
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var key = equalsIndex < 0 ? pair : pair[..equalsIndex];
            var value = equalsIndex < 0 ? string.Empty : pair[(equalsIndex + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return result;
    }

    private static void EnsureHttps(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"GitHub OAuth endpoint must use HTTPS; got '{url}'.");
        }
    }

    private static string Prefix(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(6, value.Length)];
}