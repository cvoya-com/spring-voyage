// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.SlackInstall;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Net;

/// <summary>
/// Thin HTTP wrapper around Slack's
/// <see href="https://api.slack.com/methods/apps.manifest.validate">apps.manifest.validate</see>
/// and
/// <see href="https://api.slack.com/methods/apps.manifest.create">apps.manifest.create</see>
/// endpoints. Both require a workspace-admin-issued <em>Configuration
/// Token</em>; Slack returns JSON with an <c>ok</c> boolean indicating
/// success and a structured error payload on failure.
/// </summary>
public sealed class SlackManifestApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <summary>
    /// Default Slack API base URL. Parameterized so tests can point the
    /// client at an in-process stub.
    /// </summary>
    public const string DefaultSlackBaseUrl = "https://slack.com";

    /// <summary>
    /// Creates a new client. The caller owns the <paramref name="http"/>
    /// lifetime.
    /// </summary>
    public SlackManifestApiClient(HttpClient http, string baseUrl = DefaultSlackBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? DefaultSlackBaseUrl;
    }

    /// <summary>
    /// Validates a manifest JSON against Slack's schema. Returns when
    /// Slack reports <c>ok: true</c>; throws
    /// <see cref="SlackManifestException"/> otherwise so callers can
    /// surface Slack's error code verbatim.
    /// </summary>
    public async Task ValidateAsync(
        string manifestJson,
        string configurationToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationToken);

        var url = UrlPath.Combine(_baseUrl, "/api/apps.manifest.validate");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new ManifestRequest(manifestJson)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configurationToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var envelope = ParseEnvelope(body, (int)response.StatusCode);

        if (!envelope.Ok)
        {
            throw new SlackManifestException(
                $"Slack rejected the manifest validation: {envelope.Error ?? "unknown_error"}.",
                (int)response.StatusCode,
                body,
                envelope.Error);
        }
    }

    /// <summary>
    /// Creates a new Slack app from the supplied manifest. Returns the
    /// resolved credentials Slack hands back on success (client id, client
    /// secret, signing secret, verification token, app id).
    /// </summary>
    public async Task<SlackManifestCreateResult> CreateAsync(
        string manifestJson,
        string configurationToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationToken);

        var url = UrlPath.Combine(_baseUrl, "/api/apps.manifest.create");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new ManifestRequest(manifestJson)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configurationToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var envelope = ParseEnvelope(body, (int)response.StatusCode);

        if (!envelope.Ok)
        {
            throw new SlackManifestException(
                $"Slack rejected the app creation: {envelope.Error ?? "unknown_error"}.",
                (int)response.StatusCode,
                body,
                envelope.Error);
        }

        var result = JsonSerializer.Deserialize<SlackManifestCreateResult>(body)
            ?? throw new SlackManifestException(
                "Slack returned an empty body on app creation.",
                (int)response.StatusCode,
                body,
                null);

        if (string.IsNullOrWhiteSpace(result.Credentials?.ClientSecret)
            || string.IsNullOrWhiteSpace(result.Credentials.SigningSecret))
        {
            throw new SlackManifestException(
                "Slack did not return the expected client_secret + signing_secret.",
                (int)response.StatusCode,
                body,
                null);
        }

        return result;
    }

    private static EnvelopeResult ParseEnvelope(string body, int statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            string? error = null;
            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
            {
                error = errorProp.GetString();
            }
            return new EnvelopeResult(ok, error);
        }
        catch (JsonException ex)
        {
            throw new SlackManifestException(
                $"Could not parse Slack's response as JSON: {ex.Message}. Body was: {body}",
                statusCode,
                body,
                null,
                ex);
        }
    }

    private readonly record struct EnvelopeResult(bool Ok, string? Error);

    private sealed record ManifestRequest(
        [property: JsonPropertyName("manifest")] string Manifest);
}

/// <summary>
/// DTO for Slack's <c>apps.manifest.create</c> response. Only the fields
/// the CLI actually persists are bound; other diagnostic fields are
/// ignored.
/// </summary>
public sealed class SlackManifestCreateResult
{
    [JsonPropertyName("app_id")]
    public string? AppId { get; set; }

    [JsonPropertyName("credentials")]
    public SlackManifestCredentials? Credentials { get; set; }

    [JsonPropertyName("oauth_authorize_url")]
    public string? OAuthAuthorizeUrl { get; set; }
}

/// <summary>Credentials block returned alongside the new app id.</summary>
public sealed class SlackManifestCredentials
{
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("signing_secret")]
    public string? SigningSecret { get; set; }

    [JsonPropertyName("verification_token")]
    public string? VerificationToken { get; set; }
}

/// <summary>
/// Raised when Slack rejects a manifest call or returns an unreadable
/// body. <see cref="ErrorCode"/> carries Slack's structured error code
/// (e.g. <c>invalid_manifest</c>) when available so the CLI can surface
/// the operator-facing message Slack documents.
/// </summary>
public sealed class SlackManifestException : Exception
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }
    public string? ErrorCode { get; }

    public SlackManifestException(
        string message,
        int statusCode,
        string? responseBody,
        string? errorCode,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        ErrorCode = errorCode;
    }
}
