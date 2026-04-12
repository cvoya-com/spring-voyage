// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;

using Microsoft.Extensions.Logging;

/// <summary>
/// Tier 2 (reflection) cognition provider. Uses the primary <see cref="IAiProvider"/>
/// to run the full perceive-reflect-decide loop and returns a structured
/// <see cref="ReflectionOutcome"/>.
/// </summary>
public class Tier2CognitionProvider : ICognitionProvider
{
    private readonly IAiProvider _aiProvider;
    private readonly ILogger<Tier2CognitionProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Tier2CognitionProvider"/> class.
    /// </summary>
    /// <param name="aiProvider">Primary AI provider used for the full cognition loop.</param>
    /// <param name="logger">Logger.</param>
    public Tier2CognitionProvider(IAiProvider aiProvider, ILogger<Tier2CognitionProvider> logger)
    {
        _aiProvider = aiProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<InitiativeDecision> ScreenAsync(ScreeningContext context, CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        throw new NotSupportedException("Tier 2 provider does not implement screening; call Tier 1.");
    }

    /// <inheritdoc />
    public async Task<ReflectionOutcome> ReflectAsync(ReflectionContext context, CancellationToken cancellationToken)
    {
        var prompt = BuildReflectionPrompt(context);

        string raw;
        try
        {
            raw = await _aiProvider.CompleteAsync(prompt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tier 2 primary provider call failed for agent {AgentId}; returning non-acting outcome.",
                context.AgentId);
            return new ReflectionOutcome(
                ShouldAct: false,
                ActionType: null,
                Reasoning: $"Tier 2 provider call failed: {ex.Message}",
                ActionPayload: null);
        }

        if (TryParseOutcome(raw, out var parsed))
        {
            return parsed;
        }

        _logger.LogWarning(
            "Tier 2 produced unparseable output for agent {AgentId}. Raw (truncated): {Raw}",
            context.AgentId,
            Truncate(raw, 200));

        return new ReflectionOutcome(
            ShouldAct: false,
            ActionType: null,
            Reasoning: $"Tier 2 produced unparseable output: {Truncate(raw, 200)}",
            ActionPayload: null);
    }

    private static string BuildReflectionPrompt(ReflectionContext context)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are the Tier 2 cognition loop for an autonomous agent.");
        sb.AppendLine("Consider the agent's instructions, current initiative level, accumulated observations,");
        sb.AppendLine("and the list of allowed action types. Decide whether to act and, if so, which action to take.");
        sb.AppendLine();
        sb.AppendLine("Agent instructions:");
        sb.AppendLine(context.AgentInstructions);
        sb.AppendLine();
        sb.AppendLine($"Current initiative level: {context.InitiativeLevel}");
        sb.AppendLine();
        sb.AppendLine("Observations:");

        if (context.Observations.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            for (var i = 0; i < context.Observations.Count; i++)
            {
                sb.Append('[').Append(i).Append("] ");
                sb.AppendLine(context.Observations[i].GetRawText());
            }
        }

        sb.AppendLine();
        sb.Append("Allowed actions: ");
        sb.AppendLine(context.AllowedActions.Count == 0 ? "any" : string.Join(", ", context.AllowedActions));
        sb.AppendLine();
        sb.AppendLine("Respond with a single JSON object with exactly this shape:");
        sb.AppendLine("{\"shouldAct\": <bool>, \"actionType\": <string|null>, \"reasoning\": <string>, \"actionPayload\": <object|null>}");
        sb.AppendLine("No prose, no markdown fences — JSON only.");

        return sb.ToString();
    }

    private static bool TryParseOutcome(string raw, out ReflectionOutcome outcome)
    {
        outcome = new ReflectionOutcome(false);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return false;
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("shouldAct", out var shouldActEl) ||
            (shouldActEl.ValueKind != JsonValueKind.True && shouldActEl.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        var shouldAct = shouldActEl.GetBoolean();

        string? actionType = null;
        if (root.TryGetProperty("actionType", out var actionTypeEl) &&
            actionTypeEl.ValueKind == JsonValueKind.String)
        {
            actionType = actionTypeEl.GetString();
        }

        string? reasoning = null;
        if (root.TryGetProperty("reasoning", out var reasoningEl) &&
            reasoningEl.ValueKind == JsonValueKind.String)
        {
            reasoning = reasoningEl.GetString();
        }

        JsonElement? payload = null;
        if (root.TryGetProperty("actionPayload", out var payloadEl) &&
            payloadEl.ValueKind != JsonValueKind.Null &&
            payloadEl.ValueKind != JsonValueKind.Undefined)
        {
            payload = payloadEl.Clone();
        }

        outcome = new ReflectionOutcome(shouldAct, actionType, reasoning, payload);
        return true;
    }

    /// <summary>
    /// Extracts the first balanced JSON object from <paramref name="raw"/>.
    /// Tolerates small models that wrap the JSON in prose or markdown fences.
    /// </summary>
    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return raw.Substring(start, i - start + 1);
                }
            }
        }

        return null;
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