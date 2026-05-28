// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

/// <summary>
/// HTTP wrapper around the Slack OAuth + identity endpoints needed by
/// the install / disconnect flow. Uses a named <see cref="HttpClient"/>
/// (see <see cref="HttpClientName"/>) so the host can attach a
/// credential-health watchdog without the connector taking a
/// reference on <c>Cvoya.Spring.Dapr</c>.
/// </summary>
public class SlackOAuthHttpClient : ISlackOAuthHttpClient
{
    /// <summary>
    /// Named HttpClient pulled from the
    /// <see cref="IHttpClientFactory"/> for every Slack OAuth call.
    /// Exposed as a constant so the host (per CONVENTIONS §16) can
    /// attach a credential-health watchdog by name without referencing
    /// the connector's internals.
    /// </summary>
    public const string HttpClientName = "slack-oauth";

    private const string OAuthV2Access = "https://slack.com/api/oauth.v2.access";
    private const string AuthRevoke = "https://slack.com/api/auth.revoke";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlackOAuthHttpClient> _logger;

    /// <summary>Creates a new <see cref="SlackOAuthHttpClient"/>.</summary>
    public SlackOAuthHttpClient(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = loggerFactory.CreateLogger<SlackOAuthHttpClient>();
    }

    /// <inheritdoc />
    public async Task<SlackOAuthExchangeResult> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        });

        var response = await client.PostAsync(OAuthV2Access, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<OAuthAccessResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("oauth.v2.access returned an empty body.");

        if (!dto.Ok)
        {
            _logger.LogWarning("Slack oauth.v2.access returned error={Error}", dto.Error);
            return new SlackOAuthExchangeResult(
                Ok: false,
                Error: dto.Error,
                TeamId: dto.Team?.Id ?? string.Empty,
                TeamName: dto.Team?.Name,
                BotUserId: string.Empty,
                BotAccessToken: string.Empty,
                AuthedUserId: string.Empty,
                EnterpriseId: dto.Enterprise?.Id);
        }

        return new SlackOAuthExchangeResult(
            Ok: true,
            Error: null,
            TeamId: dto.Team?.Id ?? throw new InvalidOperationException("oauth.v2.access response missing team.id"),
            TeamName: dto.Team?.Name,
            BotUserId: dto.BotUserId ?? throw new InvalidOperationException("oauth.v2.access response missing bot_user_id"),
            BotAccessToken: dto.AccessToken ?? throw new InvalidOperationException("oauth.v2.access response missing access_token"),
            AuthedUserId: dto.AuthedUser?.Id ?? throw new InvalidOperationException("oauth.v2.access response missing authed_user.id"),
            EnterpriseId: dto.Enterprise?.Id);
    }

    /// <inheritdoc />
    public async Task<SlackRevokeResult> RevokeTokenAsync(string botAccessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, AuthRevoke);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botAccessToken);
        request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());

        var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<RevokeResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("auth.revoke returned an empty body.");

        return new SlackRevokeResult(
            Ok: dto.Ok,
            Revoked: dto.Revoked,
            Error: dto.Error);
    }

    // -- DTOs mirroring Slack's wire format -------------------------------

    private sealed record OAuthAccessResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("bot_user_id")] string? BotUserId,
        [property: JsonPropertyName("team")] TeamDto? Team,
        [property: JsonPropertyName("enterprise")] EnterpriseDto? Enterprise,
        [property: JsonPropertyName("authed_user")] AuthedUserDto? AuthedUser);

    private sealed record TeamDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record EnterpriseDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record AuthedUserDto(
        [property: JsonPropertyName("id")] string Id);

    private sealed record RevokeResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("revoked")] bool Revoked,
        [property: JsonPropertyName("error")] string? Error);
}
