// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Text.Json;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestration strategy that uses an AI provider to decide how to route
/// a message to the most appropriate member within a unit.
/// </summary>
public class AiOrchestrationStrategy(
    IAiProvider aiProvider,
    ILoggerFactory loggerFactory) : IOrchestrationStrategy
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AiOrchestrationStrategy>();

    /// <inheritdoc />
    public async Task<Message?> OrchestrateAsync(Message message, IUnitContext context, CancellationToken cancellationToken = default)
    {
        if (context.Members.Count == 0)
        {
            _logger.LogWarning("Unit {UnitAddress} has no members to route to", context.UnitAddress);
            return null;
        }

        var prompt = BuildRoutingPrompt(message, context);

        _logger.LogInformation(
            "AI orchestration for message {MessageId} in unit {UnitAddress} with {MemberCount} members",
            message.Id, context.UnitAddress, context.Members.Count);

        var aiResponse = await aiProvider.CompleteAsync(prompt, cancellationToken);

        var targetAddress = ParseRoutingDecision(aiResponse, context.Members);
        if (targetAddress is null)
        {
            _logger.LogWarning(
                "AI returned an invalid routing decision for message {MessageId}: {Response}",
                message.Id, aiResponse);
            return null;
        }

        _logger.LogInformation(
            "AI routed message {MessageId} to member {TargetAddress}",
            message.Id, targetAddress);

        var forwardedMessage = message with { To = targetAddress };
        return await context.SendAsync(forwardedMessage, cancellationToken);
    }

    /// <summary>
    /// Builds the routing prompt that instructs the AI to select the best member.
    /// </summary>
    internal static string BuildRoutingPrompt(Message message, IUnitContext context)
    {
        var memberList = string.Join("\n", context.Members.Select(m => $"- {m.Scheme}://{m.Path}"));
        var payloadText = message.Payload.ValueKind != JsonValueKind.Undefined
            ? message.Payload.GetRawText()
            : "{}";

        return $"""
            You are a message router for a multi-agent unit.

            ## Message
            From: {message.From.Scheme}://{message.From.Path}
            Type: {message.Type}
            Payload: {payloadText}

            ## Available Members
            {memberList}

            Select the single best member to handle this message.
            Respond with ONLY the member address in the format: scheme://path
            """;
    }

    /// <summary>
    /// Parses the AI response to extract a target member address.
    /// Returns null if the response does not match any known member.
    /// </summary>
    internal static Address? ParseRoutingDecision(string aiResponse, IReadOnlyList<Address> members)
    {
        var trimmed = aiResponse.Trim();

        foreach (var member in members)
        {
            var memberUri = $"{member.Scheme}://{member.Path}";
            if (trimmed.Contains(memberUri, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }

        return null;
    }
}
