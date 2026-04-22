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
/// agents via the A2A (Agent-to-Agent) protocol. PR 5 of the #1087 series
/// collapsed the legacy "ephemeral agents go through
/// <c>RunAsync + harvest stdout</c>" branch onto the same A2A path that
/// persistent agents have always used:
/// <list type="number">
///   <item>Resolve image and <see cref="AgentLaunchSpec"/> via the launcher.</item>
///   <item>Build the container config via <see cref="ContainerConfigBuilder"/>.</item>
///   <item>Start the container in detached mode (<see cref="IContainerRuntime.StartAsync"/>).</item>
///   <item>Wait for the in-container A2A endpoint to become ready (<c>GET /.well-known/agent.json</c>).</item>
///   <item>Send the platform message via <see cref="SendA2AMessageAsync"/>.</item>
///   <item>Map the A2A response back to a Spring Voyage <see cref="SvMessage"/>.</item>
///   <item><b>Ephemeral</b>: tear down the container; <b>persistent</b>: leave it running.</item>
/// </list>
/// This is the change that fixes the symptom in #1087 — ephemeral agents no
/// longer get stuck on <c>sleep infinity</c> because the dispatcher no longer
/// waits for the container's stdout to terminate.
/// </summary>
public class A2AExecutionDispatcher(
    IContainerRuntime containerRuntime,
    IPromptAssembler promptAssembler,
    IAgentDefinitionProvider agentDefinitionProvider,
    IMcpServer mcpServer,
    IEnumerable<IAgentToolLauncher> launchers,
    PersistentAgentRegistry persistentAgentRegistry,
    EphemeralAgentRegistry ephemeralAgentRegistry,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IExecutionDispatcher
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<A2AExecutionDispatcher>();
    private readonly Dictionary<string, IAgentToolLauncher> _launchersByTool =
        launchers.ToDictionary(l => l.Tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default port the in-container A2A endpoint listens on. Mirrors the
    /// agent-base bridge's default and the Dapr Agent's <c>AGENT_PORT</c>.
    /// </summary>
    internal const int SidecarPort = 8999;

    /// <summary>
    /// Maximum time to wait for the in-container A2A endpoint to become ready.
    /// The bridge starts in well under a second; 60s is generous and tolerates
    /// slow-pull cold starts.
    /// </summary>
    internal static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval between readiness probe attempts.
    /// </summary>
    internal static readonly TimeSpan ReadinessProbeInterval = TimeSpan.FromMilliseconds(200);

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
            AgentHostingMode.Ephemeral => await DispatchEphemeralAsync(message, definition, context, cancellationToken),
            // Pooled is reserved on the enum (PR 1 of #1087) so agent YAML
            // written against #362 doesn't break the parser before #362
            // lands. Reject explicitly here so the value can't silently fall
            // through to ephemeral dispatch.
            AgentHostingMode.Pooled => throw new NotSupportedException(
                $"Pooled agent hosting is reserved for #362 and not yet implemented (agent '{agentId}'). " +
                "Set execution.hosting to 'ephemeral' or 'persistent'."),
            _ => throw new NotSupportedException(
                $"Unknown AgentHostingMode '{definition.Execution.Hosting}' for agent '{agentId}'."),
        };
    }

    private async Task<SvMessage?> DispatchEphemeralAsync(
        SvMessage message,
        AgentDefinition definition,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;

        if (definition.Execution!.Image is null)
        {
            // #601 B-wide: image resolution chain is agent → unit → fail. The
            // provider merges unit defaults before we see the definition here,
            // so a null image at this point means neither surface declared one.
            throw new SpringException(
                $"Ephemeral agent '{agentId}' requires a container image. " +
                "Set execution.image on the agent (spring agent execution set --image) " +
                "or on the parent unit as a default (spring unit execution set --image), " +
                "or switch the agent to hosting: persistent.");
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

        var spec = await launcher.PrepareAsync(launchContext, cancellationToken);
        var config = ContainerConfigBuilder.Build(definition.Execution.Image, spec);

        string? containerId = null;
        EphemeralAgentLease? lease = null;
        try
        {
            // Detached start: the container runs until we stop it, regardless
            // of what the agent process inside does. This is the seam that
            // fixes #1087 — the dispatcher no longer waits for the agent's
            // stdout to terminate, it talks A2A to the in-container bridge
            // and tears the container down explicitly when the turn drains.
            containerId = await containerRuntime.StartAsync(config, cancellationToken);
            lease = ephemeralAgentRegistry.Register(agentId, conversationId, containerId);

            var endpoint = new Uri($"http://localhost:{spec.A2APort}/");

            var ready = await WaitForA2AReadyAsync(
                endpoint, ReadinessTimeout, cancellationToken);

            if (!ready)
            {
                _logger.LogWarning(
                    "Ephemeral agent {AgentId} (container {ContainerId}) did not become ready within {Timeout}",
                    agentId, containerId, ReadinessTimeout);
                throw new SpringException(
                    $"Ephemeral agent '{agentId}' did not become A2A-ready within {ReadinessTimeout}.");
            }

            return await SendA2AMessageAsync(endpoint, agentId, message, prompt, cancellationToken);
        }
        finally
        {
            mcpServer.RevokeSession(session.Token);
            if (lease.HasValue)
            {
                // Detached from the caller's cancellation token — even if the
                // turn was cancelled we still want to tear the container down,
                // and the registry's release path is idempotent.
                await ephemeralAgentRegistry.ReleaseAsync(lease.Value, CancellationToken.None);
            }
            else if (containerId is not null)
            {
                // Started but never registered (extremely narrow race window
                // — Register is synchronous after StartAsync). Best-effort
                // stop so we don't leak the container.
                try
                {
                    await containerRuntime.StopAsync(containerId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to stop unregistered ephemeral container {ContainerId}", containerId);
                }
            }
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
            // #601 B-wide: same merge-aware error as the ephemeral path.
            throw new SpringException(
                $"Persistent agent '{agentId}' requires a container image. " +
                "Set execution.image on the agent (spring agent execution set --image) " +
                "or on the parent unit as a default (spring unit execution set --image).");
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

        var spec = await launcher.PrepareAsync(launchContext, cancellationToken);

        _logger.LogInformation(
            "Starting persistent agent {AgentId} with image {Image}",
            agentId, definition.Execution.Image);

        var config = ContainerConfigBuilder.Build(definition.Execution.Image, spec);

        var containerId = await containerRuntime.StartAsync(config, cancellationToken);

        var endpoint = new Uri($"http://localhost:{spec.A2APort}/");

        var ready = await WaitForA2AReadyAsync(endpoint, ReadinessTimeout, cancellationToken);

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
    /// Used by both the ephemeral and persistent dispatch paths after the
    /// in-container A2A endpoint has been observed ready.
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

    /// <summary>
    /// Polls the in-container A2A Agent Card endpoint until it answers 200
    /// or the timeout expires. Used by both dispatch paths so they cannot
    /// drift on what "ready" means.
    /// </summary>
    internal async Task<bool> WaitForA2AReadyAsync(Uri endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var agentCardUri = new Uri(endpoint, ".well-known/agent.json");
        var attempts = 0;
        Exception? lastException = null;

        while (!cts.Token.IsCancellationRequested)
        {
            attempts++;
            try
            {
                using var probeClient = httpClientFactory.CreateClient("A2A-readiness");
                probeClient.Timeout = TimeSpan.FromSeconds(5);
                var response = await probeClient.GetAsync(agentCardUri, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "A2A endpoint {Endpoint} ready after {Attempts} attempt(s)",
                        endpoint, attempts);
                    return true;
                }
                _logger.LogDebug(
                    "A2A readiness probe attempt {Attempt} for {Endpoint} returned {Status}",
                    attempts, endpoint, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogDebug(
                    "A2A readiness probe attempt {Attempt} for {Endpoint} failed: {Reason}",
                    attempts, endpoint, ex.Message);
            }

            try
            {
                await Task.Delay(ReadinessProbeInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogWarning(
            "A2A endpoint {Endpoint} did not become ready after {Attempts} attempt(s) within {Timeout}. Last error: {LastError}",
            endpoint, attempts, timeout, lastException?.Message ?? "(none)");
        return false;
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
}