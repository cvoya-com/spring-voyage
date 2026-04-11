// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Orchestration strategy that dispatches to a workflow container
/// which drives the orchestration logic externally.
/// </summary>
public class WorkflowOrchestrationStrategy(
    IContainerRuntime containerRuntime,
    ContainerLifecycleManager lifecycleManager,
    IOptions<WorkflowOrchestrationOptions> options,
    ILoggerFactory loggerFactory) : IOrchestrationStrategy
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<WorkflowOrchestrationStrategy>();
    private readonly WorkflowOrchestrationOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<Message?> OrchestrateAsync(Message message, IUnitContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Workflow orchestration for message {MessageId} in unit {UnitAddress} using image {Image}",
            message.Id, context.UnitAddress, _options.ContainerImage);

        var messageJson = JsonSerializer.Serialize(message);
        var membersJson = JsonSerializer.Serialize(context.Members);

        var config = new ContainerConfig(
            Image: _options.ContainerImage,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["SPRING_MESSAGE"] = messageJson,
                ["SPRING_MEMBERS"] = membersJson
            },
            Timeout: _options.Timeout,
            DaprEnabled: _options.DaprEnabled,
            DaprAppId: _options.DaprAppId,
            DaprAppPort: _options.DaprAppPort);

        ContainerResult result;

        if (_options.DaprEnabled)
        {
            var lifecycleResult = await lifecycleManager.LaunchWithSidecarAsync(config, cancellationToken);
            result = lifecycleResult.ContainerResult;

            // Teardown sidecar and network after the container completes.
            await lifecycleManager.TeardownAsync(
                null, lifecycleResult.SidecarInfo.SidecarId, lifecycleResult.NetworkName, CancellationToken.None);
        }
        else
        {
            result = await containerRuntime.RunAsync(config, cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            _logger.LogError(
                "Workflow container {ContainerId} exited with code {ExitCode}. Stderr: {Stderr}",
                result.ContainerId, result.ExitCode, result.StandardError);
            return null;
        }

        _logger.LogInformation(
            "Workflow container {ContainerId} completed successfully for message {MessageId}",
            result.ContainerId, message.Id);

        return ParseContainerOutput(result.StandardOutput, message);
    }

    /// <summary>
    /// Parses the container's standard output as a response message.
    /// Returns null if the output cannot be parsed.
    /// </summary>
    internal static Message? ParseContainerOutput(string output, Message originalMessage)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var payload = JsonSerializer.SerializeToElement(new { Output = output.Trim() });
        return new Message(
            Guid.NewGuid(),
            originalMessage.To,
            originalMessage.From,
            MessageType.Domain,
            originalMessage.ConversationId,
            payload,
            DateTimeOffset.UtcNow);
    }
}