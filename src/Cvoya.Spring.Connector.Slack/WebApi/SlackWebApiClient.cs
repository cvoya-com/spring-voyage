// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.WebApi;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

/// <summary>
/// HTTP-backed <see cref="ISlackWebApiClient"/>. Uses the same named
/// <see cref="HttpClient"/> the OAuth client uses
/// (<see cref="HttpClientName"/>) so the credential-health watchdog
/// observes every Slack call via a single name.
/// </summary>
public sealed class SlackWebApiClient : ISlackWebApiClient
{
    /// <summary>
    /// Named <see cref="HttpClient"/> pulled from
    /// <see cref="IHttpClientFactory"/>. Identical to the OAuth
    /// client's name so the credential-health watchdog (CONVENTIONS
    /// §16) attaches once.
    /// </summary>
    public const string HttpClientName = "slack-webapi";

    private const string SlackApiBase = "https://slack.com/api/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlackWebApiClient> _logger;

    /// <summary>Creates a new <see cref="SlackWebApiClient"/>.</summary>
    public SlackWebApiClient(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = loggerFactory.CreateLogger<SlackWebApiClient>();
    }

    /// <inheritdoc />
    public async Task<SlackOpenConversationResult> OpenConversationAsync(
        string botToken,
        string slackUserId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = BuildAuthedRequest(HttpMethod.Post, "conversations.open", botToken);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["users"] = slackUserId,
        });

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<OpenConversationDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("conversations.open returned an empty body.");

        return new SlackOpenConversationResult(
            Ok: dto.Ok,
            Error: dto.Error,
            ChannelId: dto.Channel?.Id ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<SlackPostMessageResult> PostMessageAsync(
        string botToken,
        string channel,
        string text,
        string? threadTs,
        string? username,
        string? iconUrl,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = BuildAuthedRequest(HttpMethod.Post, "chat.postMessage", botToken);

        var body = new Dictionary<string, object?>
        {
            ["channel"] = channel,
            ["text"] = text,
        };
        if (!string.IsNullOrEmpty(threadTs))
        {
            body["thread_ts"] = threadTs;
        }
        if (!string.IsNullOrEmpty(username))
        {
            body["username"] = username;
        }
        if (!string.IsNullOrEmpty(iconUrl))
        {
            body["icon_url"] = iconUrl;
        }

        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<PostMessageDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("chat.postMessage returned an empty body.");

        return new SlackPostMessageResult(
            Ok: dto.Ok,
            Error: dto.Error,
            ChannelId: dto.Channel ?? string.Empty,
            MessageTs: dto.Ts ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<SlackResult> ConversationsLeaveAsync(
        string botToken,
        string channelId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = BuildAuthedRequest(HttpMethod.Post, "conversations.leave", botToken);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["channel"] = channelId,
        });

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<SlackBaseDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("conversations.leave returned an empty body.");

        return new SlackResult(dto.Ok, dto.Error);
    }

    /// <inheritdoc />
    public async Task<SlackResult> ViewsOpenAsync(
        string botToken,
        string triggerId,
        JsonElement viewPayload,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = BuildAuthedRequest(HttpMethod.Post, "views.open", botToken);

        var body = new Dictionary<string, object?>
        {
            ["trigger_id"] = triggerId,
            ["view"] = viewPayload,
        };

        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<SlackBaseDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("views.open returned an empty body.");

        if (!dto.Ok)
        {
            _logger.LogWarning("Slack views.open returned ok=false error={Error}", dto.Error);
        }

        return new SlackResult(dto.Ok, dto.Error);
    }

    /// <inheritdoc />
    public async Task<SlackPermalinkResult> GetPermalinkAsync(
        string botToken,
        string channelId,
        string messageTs,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var url = $"chat.getPermalink?channel={Uri.EscapeDataString(channelId)}&message_ts={Uri.EscapeDataString(messageTs)}";

        using var request = BuildAuthedRequest(HttpMethod.Get, url, botToken);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<PermalinkDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("chat.getPermalink returned an empty body.");

        return new SlackPermalinkResult(
            Ok: dto.Ok,
            Error: dto.Error,
            Permalink: dto.Permalink ?? string.Empty);
    }

    private static HttpRequestMessage BuildAuthedRequest(HttpMethod method, string slackEndpoint, string botToken)
    {
        var request = new HttpRequestMessage(method, SlackApiBase + slackEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    // -- DTOs mirroring Slack's wire format -------------------------------

    private record SlackBaseDto(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record OpenConversationDto(
        bool Ok,
        string? Error,
        [property: JsonPropertyName("channel")] ChannelDto? Channel) : SlackBaseDto(Ok, Error);

    private sealed record ChannelDto(
        [property: JsonPropertyName("id")] string Id);

    private sealed record PostMessageDto(
        bool Ok,
        string? Error,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("ts")] string? Ts) : SlackBaseDto(Ok, Error);

    private sealed record PermalinkDto(
        bool Ok,
        string? Error,
        [property: JsonPropertyName("permalink")] string? Permalink) : SlackBaseDto(Ok, Error);
}
