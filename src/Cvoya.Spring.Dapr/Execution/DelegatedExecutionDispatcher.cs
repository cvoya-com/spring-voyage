/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Microsoft.Extensions.Logging;

/// <summary>
/// Dispatches agent work to external containers via the delegated execution model.
/// Only handles <see cref="ExecutionMode.Delegated"/>; throws for other modes.
/// </summary>
public class DelegatedExecutionDispatcher(
    IContainerRuntime containerRuntime,
    IPromptAssembler promptAssembler,
    ILoggerFactory loggerFactory) : IExecutionDispatcher
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DelegatedExecutionDispatcher>();

    /// <summary>
    /// Dispatches a message for delegated execution in a container.
    /// </summary>
    /// <param name="message">The message containing the work to dispatch.</param>
    /// <param name="mode">The execution mode. Must be <see cref="ExecutionMode.Delegated"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A response message containing the container output.</returns>
    /// <exception cref="SpringException">Thrown when the execution mode is not Delegated.</exception>
    public async Task<Message?> DispatchAsync(
        Message message,
        ExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode != ExecutionMode.Delegated)
        {
            throw new SpringException(
                $"DelegatedExecutionDispatcher only supports ExecutionMode.Delegated, but received {mode}.");
        }

        _logger.LogInformation(
            "Dispatching delegated execution for message {MessageId} to {Destination}",
            message.Id, message.To);

        var prompt = await promptAssembler.AssembleAsync(message, cancellationToken).ConfigureAwait(false);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_SYSTEM_PROMPT"] = prompt
        };

        var config = new ContainerConfig(
            Image: message.To.Path,
            EnvironmentVariables: envVars);

        string? containerName = null;

        // Register cancellation callback to stop the container if the token fires.
        await using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (containerName is not null)
            {
                _logger.LogWarning("Cancellation requested, stopping container {ContainerName}", containerName);
                _ = containerRuntime.StopAsync(containerName, CancellationToken.None);
            }
        });

        var result = await containerRuntime.RunAsync(config, cancellationToken).ConfigureAwait(false);
        containerName = result.ContainerId;

        _logger.LogInformation(
            "Container {ContainerId} completed with exit code {ExitCode}",
            result.ContainerId, result.ExitCode);

        return BuildResponseMessage(message, result);
    }

    private static Message BuildResponseMessage(Message originalMessage, ContainerResult result)
    {
        var payload = result.ExitCode == 0
            ? JsonSerializer.SerializeToElement(new
            {
                Output = result.StandardOutput,
                ExitCode = result.ExitCode
            })
            : JsonSerializer.SerializeToElement(new
            {
                Error = result.StandardError,
                Output = result.StandardOutput,
                ExitCode = result.ExitCode
            });

        return new Message(
            Id: Guid.NewGuid(),
            From: originalMessage.To,
            To: originalMessage.From,
            Type: MessageType.Domain,
            ConversationId: originalMessage.ConversationId,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }
}
