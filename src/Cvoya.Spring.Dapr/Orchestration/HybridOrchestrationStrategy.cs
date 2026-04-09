/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Text.Json;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Orchestration strategy that combines AI-driven classification with
/// workflow container execution. The AI decides the workflow step and
/// the container executes it.
/// </summary>
public class HybridOrchestrationStrategy(
    IAiProvider aiProvider,
    IContainerRuntime containerRuntime,
    IOptions<WorkflowOrchestrationOptions> options,
    ILoggerFactory loggerFactory) : IOrchestrationStrategy
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<HybridOrchestrationStrategy>();
    private readonly WorkflowOrchestrationOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<Message?> OrchestrateAsync(Message message, IUnitContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Hybrid orchestration for message {MessageId} in unit {UnitAddress}",
            message.Id, context.UnitAddress);

        // Phase 1: Use AI to classify the message and decide the workflow step
        var classificationPrompt = BuildClassificationPrompt(message, context);

        string classification;
        try
        {
            classification = await aiProvider.CompleteAsync(classificationPrompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AI classification failed for message {MessageId}, falling back to null response",
                message.Id);
            return null;
        }

        _logger.LogInformation(
            "AI classified message {MessageId} as: {Classification}",
            message.Id, classification.Trim());

        // Phase 2: Execute the classified step in a workflow container
        var messageJson = JsonSerializer.Serialize(message);
        var membersJson = JsonSerializer.Serialize(context.Members);

        var config = new ContainerConfig(
            Image: _options.ContainerImage,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["SPRING_MESSAGE"] = messageJson,
                ["SPRING_MEMBERS"] = membersJson,
                ["SPRING_CLASSIFICATION"] = classification.Trim()
            },
            Timeout: _options.Timeout);

        var result = await containerRuntime.RunAsync(config, cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogError(
                "Hybrid workflow container {ContainerId} exited with code {ExitCode}. Stderr: {Stderr}",
                result.ContainerId, result.ExitCode, result.StandardError);
            return null;
        }

        _logger.LogInformation(
            "Hybrid workflow container {ContainerId} completed for message {MessageId}",
            result.ContainerId, message.Id);

        return WorkflowOrchestrationStrategy.ParseContainerOutput(result.StandardOutput, message);
    }

    /// <summary>
    /// Builds the classification prompt that instructs the AI to triage the message
    /// and determine the appropriate workflow step.
    /// </summary>
    internal static string BuildClassificationPrompt(Message message, IUnitContext context)
    {
        var memberList = string.Join("\n", context.Members.Select(m => $"- {m.Scheme}://{m.Path}"));
        var payloadText = message.Payload.ValueKind != JsonValueKind.Undefined
            ? message.Payload.GetRawText()
            : "{}";

        return $"""
            You are a message classifier for a hybrid AI-workflow orchestration system.

            ## Message
            From: {message.From.Scheme}://{message.From.Path}
            Type: {message.Type}
            Payload: {payloadText}

            ## Available Members
            {memberList}

            Classify this message and determine the workflow step to execute.
            Respond with a single classification label (e.g., "process", "review", "escalate", "transform").
            """;
    }
}
