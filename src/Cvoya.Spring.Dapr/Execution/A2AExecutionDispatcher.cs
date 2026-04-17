// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using A2A;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

using A2AMessage = A2A.Message;
using SvMessage = Cvoya.Spring.Core.Messaging.Message;

/// <summary>
/// <see cref="IExecutionDispatcher"/> implementation that communicates with
/// agents via the A2A (Agent-to-Agent) protocol. For ephemeral agents the
/// dispatcher starts a container (which bundles the A2A sidecar), waits for
/// the A2A endpoint to become ready, sends a task via the A2A client SDK,
/// streams results back, and cleans up the container. For persistent agents
/// it looks up the running service in the <see cref="PersistentAgentRegistry"/>.
/// </summary>
/// <remarks>
/// This replaces <c>DelegatedExecutionDispatcher</c>. The container still
/// runs the same agent tool (Claude Code, Codex, etc.) but now behind an A2A
/// sidecar that translates the CLI stdin/stdout protocol into A2A streaming
/// events. The dispatcher consumes those events and maps them to the
/// platform's <see cref="StreamEvent"/> pipeline.
/// </remarks>
public class A2AExecutionDispatcher(
    IContainerRuntime containerRuntime,
    IPromptAssembler promptAssembler,
    IAgentDefinitionProvider agentDefinitionProvider,
    IMcpServer mcpServer,
    IEnumerable<IAgentToolLauncher> launchers,
    PersistentAgentRegistry persistentAgentRegistry,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IExecutionDispatcher
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<A2AExecutionDispatcher>();
    private readonly Dictionary<string, IAgentToolLauncher> _launchersByTool =
        launchers.ToDictionary(l => l.Tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default port the A2A sidecar listens on inside the container.
    /// </summary>
    internal const int SidecarPort = 8999;

    /// <summary>
    /// Maximum time to wait for the A2A sidecar to become ready.
    /// </summary>
    internal static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval between readiness probe attempts.
    /// </summary>
    internal static readonly TimeSpan ReadinessProbeInterval = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    public async Task<SvMessage?> DispatchAsync(
        SvMessage message,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Dispatching A2A execution for message {MessageId} to {Destination}",
            message.Id, message.To);

        var agentId = message.To.Path;
        var definition = await agentDefinitionProvider.GetByIdAsync(agentId, cancellationToken)
            ?? throw new SpringException($"No agent definition found for '{agentId}'.");

        if (definition.Execution is null)
        {
            throw new SpringException(
                $"Agent '{agentId}' has no execution configuration; set execution.tool in the agent YAML.");
        }

        return definition.Execution.Hosting switch
        {
            AgentHostingMode.Persistent => await DispatchPersistentAsync(message, definition, context, cancellationToken),
            _ => await DispatchEphemeralAsync(message, definition, context, cancellationToken),
        };
    }

    private async Task<SvMessage?> DispatchEphemeralAsync(
        SvMessage message,
        AgentDefinition definition,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;

        if (!_launchersByTool.TryGetValue(definition.Execution!.Tool, out var launcher))
        {
            throw new SpringException(
                $"No IAgentToolLauncher registered for tool '{definition.Execution.Tool}' (agent '{agentId}').");
        }

        if (mcpServer.Endpoint is null)
        {
            throw new SpringException("MCP server has not been started; endpoint is unavailable.");
        }

        var conversationId = message.ConversationId
            ?? throw new SpringException("A2A dispatch requires a conversation id on the message.");

        var prompt = await promptAssembler.AssembleAsync(message, context, cancellationToken);
        var session = mcpServer.IssueSession(agentId, conversationId);
        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ConversationId: conversationId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model);

        var prep = await launcher.PrepareAsync(launchContext, cancellationToken);

        try
        {
            if (definition.Execution.Image is null)
            {
                throw new SpringException(
                    $"Ephemeral agent '{agentId}' requires a container image. " +
                    "Set execution.image in the agent YAML or use hosting: persistent.");
            }

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
                    _logger.LogWarning(
                        "Cancellation requested, stopping container {ContainerName}", containerName);
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

    private async Task<SvMessage?> DispatchPersistentAsync(
        SvMessage message,
        AgentDefinition definition,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;

        // Check if the agent service is already running and healthy.
        if (!persistentAgentRegistry.TryGetEndpoint(agentId, out var endpoint) || endpoint is null)
        {
            // Not running — auto-start the agent container.
            endpoint = await StartPersistentAgentAsync(definition, cancellationToken);
        }

        var prompt = await promptAssembler.AssembleAsync(message, context, cancellationToken);

        try
        {
            return await SendA2AMessageAsync(endpoint, agentId, message, prompt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Container failed mid-dispatch — mark unhealthy for next dispatch.
            _logger.LogWarning(ex,
                "A2A call to persistent agent {AgentId} failed; marking unhealthy for restart",
                agentId);
            persistentAgentRegistry.MarkUnhealthy(agentId);
            throw;
        }
    }

    /// <summary>
    /// Starts a persistent agent container and registers it in the registry.
    /// </summary>
    private async Task<Uri> StartPersistentAgentAsync(
        AgentDefinition definition,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;

        if (definition.Execution?.Image is null)
        {
            throw new SpringException(
                $"Persistent agent '{agentId}' requires a container image. " +
                "Set execution.image in the agent YAML.");
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

        // Use a stable conversation ID for persistent agent MCP sessions.
        var sessionId = $"persistent-{agentId}";
        var prompt = definition.Instructions ?? string.Empty;
        var session = mcpServer.IssueSession(agentId, sessionId);

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ConversationId: sessionId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model);

        var prep = await launcher.PrepareAsync(launchContext, cancellationToken);

        _logger.LogInformation(
            "Starting persistent agent {AgentId} with image {Image}",
            agentId, definition.Execution.Image);

        var config = new ContainerConfig(
            Image: definition.Execution.Image,
            EnvironmentVariables: prep.EnvironmentVariables,
            VolumeMounts: prep.VolumeMounts,
            ExtraHosts: ["host.docker.internal:host-gateway"],
            WorkingDirectory: prep.WorkingDirectory.Contains(':')
                ? null // Volume mount spec — don't set working dir
                : prep.WorkingDirectory);

        var containerId = await containerRuntime.StartAsync(config, cancellationToken);

        // Build the A2A endpoint — persistent containers expose the A2A sidecar port.
        // Use localhost with a mapped port since the container name may not be DNS-resolvable.
        var endpoint = new Uri($"http://localhost:{SidecarPort}/");

        // Wait for the A2A endpoint to become ready.
        var ready = await persistentAgentRegistry.WaitForA2AReadyAsync(
            endpoint, ReadinessTimeout, cancellationToken);

        if (!ready)
        {
            _logger.LogError(
                "Persistent agent {AgentId} did not become ready within {Timeout}. Stopping container.",
                agentId, ReadinessTimeout);
            await containerRuntime.StopAsync(containerId, CancellationToken.None);
            throw new SpringException(
                $"Persistent agent '{agentId}' did not become ready within {ReadinessTimeout}.");
        }

        // Register in the persistent registry.
        persistentAgentRegistry.Register(agentId, endpoint, containerId, definition);

        _logger.LogInformation(
            "Persistent agent {AgentId} started and registered at {Endpoint} (container {ContainerId})",
            agentId, endpoint, containerId);

        return endpoint;
    }

    /// <summary>
    /// Sends a message to a running A2A agent and collects the response.
    /// Used by the persistent path and will be used by the ephemeral path
    /// once the sidecar exposes the A2A endpoint from inside the container.
    /// </summary>
    internal async Task<SvMessage?> SendA2AMessageAsync(
        Uri endpoint,
        string agentId,
        SvMessage originalMessage,
        string prompt,
        CancellationToken cancellationToken)
    {
        using var httpClient = httpClientFactory.CreateClient($"A2A-{agentId}");
        var a2aClient = new A2AClient(endpoint, httpClient);

        var userMessage = prompt;
        if (originalMessage.Payload.ValueKind == JsonValueKind.Object &&
            originalMessage.Payload.TryGetProperty("Task", out var taskProp) &&
            taskProp.ValueKind == JsonValueKind.String)
        {
            userMessage = taskProp.GetString() ?? prompt;
        }

        var request = new SendMessageRequest
        {
            Message = new A2AMessage
            {
                Role = Role.User,
                Parts = [new Part { Text = userMessage }],
                MessageId = originalMessage.Id.ToString(),
                ContextId = originalMessage.ConversationId,
            },
            Configuration = new SendMessageConfiguration
            {
                AcceptedOutputModes = ["text/plain"],
            },
        };

        var response = await a2aClient.SendMessageAsync(request, cancellationToken);

        return MapA2AResponseToMessage(originalMessage, response);
    }

    internal static SvMessage? MapA2AResponseToMessage(
        SvMessage originalMessage,
        SendMessageResponse response)
    {
        string output;
        int exitCode;

        switch (response.PayloadCase)
        {
            case SendMessageResponseCase.Task:
                var task = response.Task!;
                exitCode = task.Status?.State is TaskState.Completed ? 0 : 1;
                output = ExtractTextFromTask(task);
                break;

            case SendMessageResponseCase.Message:
                var msg = response.Message!;
                exitCode = 0;
                output = ExtractTextFromParts(msg.Parts);
                break;

            default:
                exitCode = 1;
                output = "No response from A2A agent.";
                break;
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            Output = output,
            ExitCode = exitCode
        });

        return new SvMessage(
            Id: Guid.NewGuid(),
            From: originalMessage.To,
            To: originalMessage.From,
            Type: MessageType.Domain,
            ConversationId: originalMessage.ConversationId,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static string ExtractTextFromTask(AgentTask task)
    {
        // First try artifacts
        if (task.Artifacts is { Count: > 0 })
        {
            var texts = task.Artifacts
                .SelectMany(a => a.Parts ?? [])
                .Where(p => p.ContentCase == PartContentCase.Text)
                .Select(p => p.Text)
                .Where(t => t is not null);
            var artifactText = string.Join("\n", texts);
            if (!string.IsNullOrEmpty(artifactText))
            {
                return artifactText;
            }
        }

        // Fall back to status message
        if (task.Status?.Message is { } statusMsg)
        {
            return ExtractTextFromParts(statusMsg.Parts);
        }

        // Fall back to history
        if (task.History is { Count: > 0 })
        {
            var lastAgent = task.History.LastOrDefault(m => m.Role == Role.Agent);
            if (lastAgent is not null)
            {
                return ExtractTextFromParts(lastAgent.Parts);
            }
        }

        return string.Empty;
    }

    private static string ExtractTextFromParts(IReadOnlyList<Part>? parts)
    {
        if (parts is null or { Count: 0 })
        {
            return string.Empty;
        }

        return string.Join("\n", parts
            .Where(p => p.ContentCase == PartContentCase.Text)
            .Select(p => p.Text)
            .Where(t => t is not null));
    }

    private static SvMessage BuildResponseMessage(SvMessage originalMessage, ContainerResult result)
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

        return new SvMessage(
            Id: Guid.NewGuid(),
            From: originalMessage.To,
            To: originalMessage.From,
            Type: MessageType.Domain,
            ConversationId: originalMessage.ConversationId,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }
}