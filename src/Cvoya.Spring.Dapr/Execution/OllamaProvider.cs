// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IAiProvider"/> implementation that targets an Ollama server through its
/// OpenAI-compatible <c>/v1/chat/completions</c> endpoint. No API key is required; the
/// endpoint shape is identical to hosted OpenAI so the same request/response handling
/// applies to both.
/// </summary>
/// <remarks>
/// <para>
/// Agentic / tool-use flows go through <see cref="IExecutionDispatcher"/> and the
/// external agent runtime (Claude Code, etc.) — this provider is for lightweight
/// single-shot completions and token streaming, matching the
/// <see cref="AnthropicProvider"/> scope.
/// </para>
/// <para>
/// Extension points: the class is not sealed and HTTP/JSON parsing helpers are
/// <c>protected virtual</c> so the private cloud repo can override request shaping
/// (e.g. tenant headers) or response interpretation (e.g. tool-call extraction) without
/// forking the provider.
/// </para>
/// </remarks>
public class OllamaProvider(
    HttpClient httpClient,
    IOptions<OllamaOptions> options,
    ILoggerFactory loggerFactory) : IAiProvider
{
    /// <summary>
    /// Stable provider id; matches the manifest's <c>execution.provider</c>
    /// slot value when the operator selects Ollama.
    /// </summary>
    public const string ProviderId = "ollama";

    /// <inheritdoc />
    public string Id => ProviderId;

    private readonly ILogger _logger = loggerFactory.CreateLogger<OllamaProvider>();
    private readonly OllamaOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            model = _options.DefaultModel,
            max_tokens = _options.MaxTokens,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri())
        {
            Content = JsonContent.Create(requestBody)
        };

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
            throw new SpringException(
                $"Ollama request to {_options.BaseUrl} failed. Is the server reachable? ({ex.Message})",
                ex);
        }

        try
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new SpringException(
                    $"Ollama API returned {(int)response.StatusCode} {response.StatusCode}. Response: {errorBody}");
            }

            return await ParseResponseAsync(response, cancellationToken);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> StreamCompleteAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            model = _options.DefaultModel,
            max_tokens = _options.MaxTokens,
            stream = true,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri())
        {
            Content = JsonContent.Create(requestBody)
        };

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SpringException(
                $"Ollama streaming API returned {(int)response.StatusCode} {response.StatusCode}. Response: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var inputTokens = 0;
        var outputTokens = 0;
        string? stopReason = null;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                break;
            }

            JsonElement chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<JsonElement>(data);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse Ollama SSE chunk.");
                continue;
            }

            if (chunk.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new StreamEvent.TokenDelta(
                            Guid.NewGuid(),
                            DateTimeOffset.UtcNow,
                            text);
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                    finishReason.ValueKind == JsonValueKind.String)
                {
                    stopReason = finishReason.GetString();
                }
            }

            if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                {
                    inputTokens = pt.GetInt32();
                }

                if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                {
                    outputTokens = ct.GetInt32();
                }
            }
        }

        yield return new StreamEvent.Completed(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            inputTokens,
            outputTokens,
            stopReason);

        _logger.LogInformation(
            "Ollama streaming completed — input: {InputTokens}, output: {OutputTokens}, stop: {StopReason}",
            inputTokens, outputTokens, stopReason);
    }

    /// <summary>
    /// Builds the full URI for the OpenAI-compatible chat-completions endpoint. The
    /// trailing slash on <see cref="OllamaOptions.BaseUrl"/> is tolerated — both
    /// <c>http://host:11434</c> and <c>http://host:11434/</c> resolve the same way.
    /// </summary>
    protected virtual Uri BuildChatCompletionsUri()
    {
        return new Uri(UrlPath.Combine(_options.BaseUrl, "/v1/chat/completions"));
    }

    /// <summary>
    /// Extracts the assistant message from an OpenAI-compatible chat-completion response.
    /// Override to add custom post-processing such as tool-call extraction.
    /// </summary>
    protected virtual async Task<string> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (json.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var input = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number
                ? pt.GetInt32() : 0;
            var output = usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number
                ? ct.GetInt32() : 0;
            _logger.LogInformation("Ollama usage — input: {InputTokens}, output: {OutputTokens}", input, output);
        }

        if (json.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? throw new SpringException("Ollama response contained null content.");
            }
        }

        throw new SpringException("Ollama response did not contain a message choice.");
    }
}
