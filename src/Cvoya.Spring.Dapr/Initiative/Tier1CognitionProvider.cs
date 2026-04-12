// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Tier 1 (screening) cognition provider. Uses a cheap local Ollama model for fast
/// event triage and falls back to the primary <see cref="IAiProvider"/> when Ollama
/// is disabled or unreachable.
/// </summary>
public class Tier1CognitionProvider : ICognitionProvider
{
    private const string ScreeningSystemRubric =
        "You are a fast event screener for an autonomous agent. Respond with exactly one token: "
        + "\"ignore\" if the event is irrelevant, \"queue\" if the event is potentially relevant but "
        + "does not require immediate action, or \"act\" if the event requires immediate reflection. "
        + "No explanation. No punctuation.";

    private readonly HttpClient _httpClient;
    private readonly IAiProvider _fallback;
    private readonly Tier1Options _options;
    private readonly ILogger<Tier1CognitionProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Tier1CognitionProvider"/> class.
    /// </summary>
    /// <param name="httpClient">Named HTTP client configured for the Ollama endpoint.</param>
    /// <param name="fallback">Primary AI provider used when Ollama is disabled or unreachable.</param>
    /// <param name="options">Tier 1 configuration options.</param>
    /// <param name="logger">Logger.</param>
    public Tier1CognitionProvider(
        HttpClient httpClient,
        IAiProvider fallback,
        IOptions<Tier1Options> options,
        ILogger<Tier1CognitionProvider> logger)
    {
        _httpClient = httpClient;
        _fallback = fallback;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InitiativeDecision> ScreenAsync(ScreeningContext context, CancellationToken cancellationToken)
    {
        var prompt = BuildScreeningPrompt(context);

        if (!_options.Enabled)
        {
            _logger.LogDebug("Tier 1 Ollama disabled; routing screening through fallback provider.");
            return await ScreenViaFallbackAsync(prompt, cancellationToken);
        }

        string rawResponse;
        try
        {
            rawResponse = await CallOllamaAsync(prompt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            _logger.LogWarning(ex,
                "Tier 1 Ollama connection refused at {BaseUrl}; falling back to primary provider.",
                _options.OllamaBaseUrl);
            return await ScreenViaFallbackAsync(prompt, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Tier 1 Ollama request failed; defaulting to QueueForReflection. Agent: {AgentId}",
                context.AgentId);
            return InitiativeDecision.QueueForReflection;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Tier 1 Ollama response was not valid JSON; defaulting to QueueForReflection. Agent: {AgentId}",
                context.AgentId);
            return InitiativeDecision.QueueForReflection;
        }

        return ParseDecision(rawResponse, context.AgentId);
    }

    /// <inheritdoc />
    public Task<ReflectionOutcome> ReflectAsync(ReflectionContext context, CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        throw new NotSupportedException("Tier 1 provider does not implement reflection; call Tier 2.");
    }

    private async Task<InitiativeDecision> ScreenViaFallbackAsync(string prompt, CancellationToken cancellationToken)
    {
        string response;
        try
        {
            response = await _fallback.CompleteAsync(prompt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tier 1 fallback provider failed; defaulting to QueueForReflection.");
            return InitiativeDecision.QueueForReflection;
        }

        return ParseDecision(response, agentId: null);
    }

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken cancellationToken)
    {
        var endpoint = $"{_options.OllamaBaseUrl.TrimEnd('/')}/api/generate";

        var body = new
        {
            model = _options.Model,
            prompt,
            stream = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (!payload.TryGetProperty("response", out var responseElement))
        {
            throw new JsonException("Ollama response payload did not contain a 'response' field.");
        }

        return responseElement.GetString() ?? string.Empty;
    }

    private InitiativeDecision ParseDecision(string rawResponse, string? agentId)
    {
        var normalized = rawResponse.Trim().Trim('.', ',', '!', '?', '"', '\'').ToLowerInvariant();

        if (normalized.Length == 0)
        {
            _logger.LogWarning("Tier 1 screening returned empty response; defaulting to QueueForReflection. Agent: {AgentId}", agentId);
            return InitiativeDecision.QueueForReflection;
        }

        // Take first whitespace-delimited token so small models adding trailing text still parse.
        var firstToken = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

        return firstToken switch
        {
            "ignore" => InitiativeDecision.Ignore,
            "queue" => InitiativeDecision.QueueForReflection,
            "act" => InitiativeDecision.ActImmediately,
            _ => LogAndDefault(rawResponse, agentId)
        };
    }

    private InitiativeDecision LogAndDefault(string rawResponse, string? agentId)
    {
        _logger.LogWarning(
            "Tier 1 screening produced unrecognised token; defaulting to QueueForReflection. Agent: {AgentId}, raw: {Raw}",
            agentId,
            Truncate(rawResponse, 120));
        return InitiativeDecision.QueueForReflection;
    }

    private static string BuildScreeningPrompt(ScreeningContext context)
    {
        var payloadLine = context.EventPayload.HasValue
            ? context.EventPayload.Value.GetRawText()
            : "(no payload)";

        return string.Join(
            '\n',
            ScreeningSystemRubric,
            string.Empty,
            $"Agent: {context.AgentId}",
            $"Initiative level: {context.InitiativeLevel}",
            $"Agent instructions: {context.AgentInstructions}",
            string.Empty,
            $"Event: {context.EventSummary}",
            $"Payload: {payloadLine}",
            string.Empty,
            "Decision:");
    }

    private static bool IsConnectionRefused(HttpRequestException ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                (socket.SocketErrorCode == SocketError.ConnectionRefused
                    || socket.SocketErrorCode == SocketError.HostNotFound
                    || socket.SocketErrorCode == SocketError.HostUnreachable
                    || socket.SocketErrorCode == SocketError.NetworkUnreachable
                    || socket.SocketErrorCode == SocketError.TimedOut))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }
}

/// <summary>
/// Options controlling the Tier 1 screening provider's Ollama integration.
/// Public to satisfy C# accessibility rules because it appears as an <see cref="IOptions{T}"/>
/// parameter on the public <see cref="Tier1CognitionProvider"/> constructor.
/// </summary>
/// <param name="OllamaBaseUrl">Base URL for the Ollama HTTP API.</param>
/// <param name="Model">Model identifier to pass to Ollama's generate endpoint.</param>
/// <param name="Enabled">When <c>false</c>, screening always routes through the fallback provider.</param>
public record Tier1Options(
    string OllamaBaseUrl = "http://localhost:11434",
    string Model = "phi-3-mini",
    bool Enabled = true);