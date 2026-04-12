// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dispatches agent work to an external agent-tool container. Resolves the
/// container image and tool from the agent definition, issues an MCP session
/// for the container to dial back into, and delegates the
/// tool-specific working-directory materialisation to the matching
/// <see cref="IAgentToolLauncher"/>.
/// </summary>
/// <remarks>
/// The destination <see cref="Message.To"/> of a dispatch message must use the
/// <c>agent</c> scheme and its path must match an
/// <see cref="IAgentDefinitionProvider"/> id. The image is taken from
/// <see cref="AgentExecutionConfig.Image"/>; the dispatcher does not
/// interpret <see cref="Message.To.Path"/> as an image reference.
/// </remarks>
public class DelegatedExecutionDispatcher(
    IContainerRuntime containerRuntime,
    IPromptAssembler promptAssembler,
    IAgentDefinitionProvider agentDefinitionProvider,
    IMcpServer mcpServer,
    IEnumerable<IAgentToolLauncher> launchers,
    ILoggerFactory loggerFactory) : IExecutionDispatcher
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DelegatedExecutionDispatcher>();
    private readonly Dictionary<string, IAgentToolLauncher> _launchersByTool =
        launchers.ToDictionary(l => l.Tool, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<Message?> DispatchAsync(
        Message message,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Dispatching execution for message {MessageId} to {Destination}",
            message.Id, message.To);

        var agentId = message.To.Path;
        var definition = await agentDefinitionProvider.GetByIdAsync(agentId, cancellationToken)
            ?? throw new SpringException($"No agent definition found for '{agentId}'.");

        if (definition.Execution is null)
        {
            throw new SpringException(
                $"Agent '{agentId}' has no execution configuration; set execution.tool and execution.image in the agent YAML.");
        }

        if (!_launchersByTool.TryGetValue(definition.Execution.Tool, out var launcher))
        {
            throw new SpringException(
                $"No IAgentToolLauncher registered for tool '{definition.Execution.Tool}' (agent '{agentId}').");
        }

        if (mcpServer.Endpoint is null)
        {
            throw new SpringException("MCP server has not been started; endpoint is unavailable.");
        }

        var conversationId = message.ConversationId
            ?? throw new SpringException("Delegated dispatch requires a conversation id on the message.");

        var prompt = await promptAssembler.AssembleAsync(message, context, cancellationToken);

        var session = mcpServer.IssueSession(agentId, conversationId);
        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ConversationId: conversationId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token);

        var prep = await launcher.PrepareAsync(launchContext, cancellationToken);

        try
        {
            var config = new ContainerConfig(
                Image: definition.Execution.Image,
                EnvironmentVariables: prep.EnvironmentVariables,
                VolumeMounts: prep.VolumeMounts,
                ExtraHosts: ["host.docker.internal:host-gateway"],
                WorkingDirectory: ClaudeCodeLauncher.WorkspaceMountPath);

            string? containerName = null;
            await using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (containerName is not null)
                {
                    _logger.LogWarning("Cancellation requested, stopping container {ContainerName}", containerName);
                    _ = containerRuntime.StopAsync(containerName, CancellationToken.None);
                }
            });

            var result = await containerRuntime.RunAsync(config, cancellationToken);
            containerName = result.ContainerId;

            _logger.LogInformation(
                "Container {ContainerId} (agent {AgentId}) completed with exit code {ExitCode}",
                result.ContainerId, agentId, result.ExitCode);

            return BuildResponseMessage(message, result);
        }
        finally
        {
            mcpServer.RevokeSession(session.Token);
            await launcher.CleanupAsync(prep.WorkingDirectory, CancellationToken.None);
        }
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