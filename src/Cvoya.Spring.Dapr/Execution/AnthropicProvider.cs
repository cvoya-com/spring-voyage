// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// AI provider implementation that calls the Anthropic Messages API.
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

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
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
    public async Task<AiResponse> CompleteWithToolsAsync(
        IReadOnlyList<ConversationTurn> turns,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(tools);

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["messages"] = turns.Select(SerializeTurn).ToArray(),
        };

        if (tools.Count > 0)
        {
            requestBody["tools"] = tools.Select(SerializeTool).ToArray();
        }

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
                    _logger.LogError(ex, "Anthropic tool-use API request failed after {Attempts} attempts.", MaxRetries);
                    throw new SpringException($"Anthropic API request failed after {MaxRetries} attempts.", ex);
                }

                _logger.LogWarning(ex, "Anthropic tool-use API request failed on attempt {Attempt}, retrying.", attempt);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                if (attempt >= MaxRetries)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError(
                        "Anthropic tool-use API returned {StatusCode} after {Attempts} attempts. Body: {Body}",
                        response.StatusCode, MaxRetries, errorBody);
                    throw new SpringException(
                        $"Anthropic API returned {response.StatusCode} after {MaxRetries} attempts. Response: {errorBody}");
                }

                _logger.LogWarning("Anthropic tool-use API returned {StatusCode} on attempt {Attempt}, retrying.",
                    response.StatusCode, attempt);
                response.Dispose();
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Anthropic tool-use API returned {StatusCode}. Body: {Body}",
                    response.StatusCode, errorBody);
                throw new SpringException(
                    $"Anthropic API returned {response.StatusCode}. Response: {errorBody}");
            }

            return await ParseToolUseResponseAsync(response, cancellationToken);
        }
    }

    private async Task<AiResponse> ParseToolUseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (json.TryGetProperty("usage", out var usage))
        {
            var inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            var outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            _logger.LogInformation("Anthropic tool-use usage — input: {InputTokens}, output: {OutputTokens}",
                inputTokens, outputTokens);
        }

        var stopReason = json.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";

        var textBuilder = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        if (json.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                switch (blockType)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var textElement))
                        {
                            textBuilder.Append(textElement.GetString());
                        }

                        break;

                    case "tool_use":
                        var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                        var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                        var input = block.TryGetProperty("input", out var inputEl)
                            ? inputEl.Clone()
                            : JsonSerializer.SerializeToElement(new { });
                        toolCalls.Add(new ToolCall(id, name, input));
                        break;
                }
            }
        }

        var text = textBuilder.Length > 0 ? textBuilder.ToString() : null;
        return new AiResponse(text, toolCalls, stopReason);
    }

    private static object SerializeTurn(ConversationTurn turn)
    {
        return new
        {
            role = turn.Role,
            content = turn.Content.Select(SerializeBlock).ToArray(),
        };
    }

    private static object SerializeBlock(ContentBlock block)
    {
        return block switch
        {
            ContentBlock.TextBlock text => new { type = "text", text = text.Text },
            ContentBlock.ToolUseBlock toolUse => (object)new
            {
                type = "tool_use",
                id = toolUse.Id,
                name = toolUse.Name,
                input = toolUse.Input,
            },
            ContentBlock.ToolResultBlock toolResult => new
            {
                type = "tool_result",
                tool_use_id = toolResult.ToolUseId,
                content = toolResult.Content,
                is_error = toolResult.IsError,
            },
            _ => throw new SpringException($"Unsupported content block type: {block.GetType().Name}"),
        };
    }

    private static object SerializeTool(ToolDefinition tool)
    {
        return new
        {
            name = tool.Name,
            description = tool.Description,
            input_schema = tool.InputSchema,
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> StreamCompleteAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
        public string? CurrentToolName { get; set; }
    }

    private static List<StreamEvent> ParseSseEvent(
        string? eventType,
        JsonElement eventJson,
        SseParseState state)
    {
        var events = new List<StreamEvent>();

        switch (eventType)
        {
            case "content_block_start":
                if (eventJson.TryGetProperty("content_block", out var contentBlock))
                {
                    var blockType = contentBlock.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                    if (blockType == "tool_use")
                    {
                        state.CurrentToolName = contentBlock.TryGetProperty("name", out var name) ? name.GetString() : "unknown";
                        var toolInput = contentBlock.TryGetProperty("input", out var input) ? input.ToString() : "{}";
                        events.Add(new StreamEvent.ToolCallStart(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            state.CurrentToolName ?? "unknown",
                            toolInput));
                    }
                }

                break;

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
                    else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var partialJson))
                    {
                        events.Add(new StreamEvent.OutputDelta(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            partialJson.GetString() ?? string.Empty));
                    }
                }

                break;

            case "content_block_stop":
                if (state.CurrentToolName is not null)
                {
                    events.Add(new StreamEvent.ToolCallResult(
                        Guid.NewGuid(),
                        DateTimeOffset.UtcNow,
                        state.CurrentToolName,
                        "completed"));
                    state.CurrentToolName = null;
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
}