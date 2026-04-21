// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// AI provider implementation that calls the Anthropic Messages API for lightweight
/// single-shot and streaming completions. Agentic / tool-use work is delegated to
/// external agent runtimes via <see cref="IExecutionDispatcher"/> and is outside the
/// scope of this provider.
/// </summary>
public class AnthropicProvider(
    HttpClient httpClient,
    IOptions<AiProviderOptions> options,
    ILoggerFactory loggerFactory) : IAiProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AnthropicProvider>();
    private readonly AiProviderOptions _options = options.Value;

    private const string AnthropicVersion = "2023-06-01";
    private const int MaxRetries = 3;

    /// <summary>
    /// Credential prefix identifying a Claude.ai OAuth token produced by
    /// <c>claude setup-token</c>. The Anthropic Platform REST endpoint
    /// rejects this format — OAuth tokens are only usable through the
    /// <c>claude</c> CLI running inside a unit container via the
    /// <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime"/> dispatch
    /// path. Detected here so we fail fast with an operator-actionable
    /// message instead of surfacing a silent upstream 401 as a 502.
    /// </summary>
    private const string OAuthTokenPrefix = "sk-ant-oat";

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        RejectOAuthToken(_options.ApiKey);

        var requestBody = new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var attempt = 0;
        while (true)
        {
            attempt++;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/v1/messages");
            request.Headers.Add("x-api-key", _options.ApiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Content = JsonContent.Create(requestBody);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxRetries)
                {
                    throw new SpringException($"Anthropic API request failed after {MaxRetries} attempts.", ex);
                }

                _logger.LogWarning(ex, "Anthropic API request failed on attempt {Attempt}, retrying.", attempt);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt >= MaxRetries)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new SpringException(
                        $"Anthropic API returned {response.StatusCode} after {MaxRetries} attempts. Response: {errorBody}");
                }

                _logger.LogWarning("Anthropic API returned {StatusCode} on attempt {Attempt}, retrying.",
                    response.StatusCode, attempt);
                response.Dispose();
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new SpringException(
                    $"Anthropic API returned {response.StatusCode}. Response: {errorBody}");
            }

            return await ParseResponseAsync(response, cancellationToken);
        }
    }

    private async Task<string> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (json.TryGetProperty("usage", out var usage))
        {
            var stats = new UsageStats(
                usage.GetProperty("input_tokens").GetInt32(),
                usage.GetProperty("output_tokens").GetInt32());
            _logger.LogInformation("Anthropic usage — input: {InputTokens}, output: {OutputTokens}",
                stats.InputTokens, stats.OutputTokens);
        }

        if (json.TryGetProperty("content", out var content) &&
            content.GetArrayLength() > 0)
        {
            return content[0].GetProperty("text").GetString()
                   ?? throw new SpringException("Anthropic API returned null text in content.");
        }

        throw new SpringException("Anthropic API response did not contain expected content.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> StreamCompleteAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RejectOAuthToken(_options.ApiKey);

        var requestBody = new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,
            stream = true,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/v1/messages");
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Content = JsonContent.Create(requestBody);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SpringException($"Anthropic streaming API returned {response.StatusCode}. Response: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var state = new SseParseState();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];

            if (data == "[DONE]")
            {
                break;
            }

            JsonElement eventJson;
            try
            {
                eventJson = JsonSerializer.Deserialize<JsonElement>(data);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse SSE event data.");
                continue;
            }

            if (!eventJson.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var eventType = typeElement.GetString();

            foreach (var streamEvent in ParseSseEvent(eventType, eventJson, state))
            {
                yield return streamEvent;
            }
        }

        yield return new StreamEvent.Completed(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            state.InputTokens,
            state.OutputTokens,
            state.StopReason);

        _logger.LogInformation("Streaming completed — input: {InputTokens}, output: {OutputTokens}, stop: {StopReason}",
            state.InputTokens, state.OutputTokens, state.StopReason);
    }

    /// <summary>
    /// Mutable state tracker for SSE stream parsing.
    /// </summary>
    private sealed class SseParseState
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public string? StopReason { get; set; }
    }

    private static List<StreamEvent> ParseSseEvent(
        string? eventType,
        JsonElement eventJson,
        SseParseState state)
    {
        var events = new List<StreamEvent>();

        switch (eventType)
        {
            case "content_block_delta":
                if (eventJson.TryGetProperty("delta", out var delta))
                {
                    var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;

                    if (deltaType == "text_delta" && delta.TryGetProperty("text", out var text))
                    {
                        events.Add(new StreamEvent.TokenDelta(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            text.GetString() ?? string.Empty));
                    }
                    else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinking))
                    {
                        events.Add(new StreamEvent.ThinkingDelta(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            thinking.GetString() ?? string.Empty));
                    }
                }

                break;

            case "message_start":
                if (eventJson.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("usage", out var startUsage))
                {
                    if (startUsage.TryGetProperty("input_tokens", out var it))
                    {
                        state.InputTokens = it.GetInt32();
                    }
                }

                break;

            case "message_delta":
                if (eventJson.TryGetProperty("usage", out var deltaUsage))
                {
                    if (deltaUsage.TryGetProperty("output_tokens", out var ot))
                    {
                        state.OutputTokens = ot.GetInt32();
                    }
                }

                if (eventJson.TryGetProperty("delta", out var msgDelta) &&
                    msgDelta.TryGetProperty("stop_reason", out var sr))
                {
                    state.StopReason = sr.GetString();
                }

                break;
        }

        return events;
    }

    private static async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
        await Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Guards the REST call against Claude.ai OAuth tokens. The Anthropic
    /// Platform endpoint rejects OAuth tokens with a 401 that the generic
    /// error path would surface as a silent 502 on the first user message
    /// (see #981). OAuth tokens are usable only through the <c>claude</c>
    /// CLI running inside a unit container via the
    /// <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime"/> dispatch
    /// path — this <see cref="IAiProvider"/> implementation is host-side
    /// REST only (ADR 0021) and so cannot serve them.
    /// </summary>
    private static void RejectOAuthToken(string? credential)
    {
        if (credential is not null
            && credential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal))
        {
            throw new SpringException(
                "CredentialFormatRejected: the AiProvider credential is a Claude.ai OAuth token (sk-ant-oat…), " +
                "which the Anthropic Platform REST endpoint rejects. " +
                "OAuth tokens are only usable through the `claude` CLI running inside a unit container — " +
                "supply an Anthropic Platform API key (sk-ant-api…) for `AiProvider:ApiKey`, " +
                "or keep the OAuth token as a unit/tenant secret (anthropic-api-key) used by the Claude agent runtime.");
        }
    }
}