// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.Extensions.Logging;

/// <summary>
/// Execution dispatcher that handles hosted mode by assembling a prompt
/// and calling an AI provider in-process. Supports both non-streaming and
/// streaming execution paths, and implements a multi-turn tool-use loop
/// when the provider returns tool calls.
/// </summary>
public class HostedExecutionDispatcher : IExecutionDispatcher
{
    /// <summary>
    /// The maximum number of tool-use iterations to perform before aborting the loop.
    /// </summary>
    public const int MaxToolIterations = 10;

    private readonly IAiProvider _aiProvider;
    private readonly IPromptAssembler _promptAssembler;
    private readonly StreamEventPublisher? _streamEventPublisher;
    private readonly IReadOnlyList<ISkillToolExecutor> _toolExecutors;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="HostedExecutionDispatcher"/>.
    /// </summary>
    public HostedExecutionDispatcher(
        IAiProvider aiProvider,
        IPromptAssembler promptAssembler,
        IEnumerable<ISkillToolExecutor> toolExecutors,
        StreamEventPublisher? streamEventPublisher,
        ILoggerFactory loggerFactory)
    {
        _aiProvider = aiProvider;
        _promptAssembler = promptAssembler;
        _streamEventPublisher = streamEventPublisher;
        _toolExecutors = toolExecutors?.ToArray() ?? Array.Empty<ISkillToolExecutor>();
        _logger = loggerFactory.CreateLogger<HostedExecutionDispatcher>();
    }

    /// <summary>
    /// Initializes a new instance without a stream event publisher (non-streaming only).
    /// </summary>
    public HostedExecutionDispatcher(
        IAiProvider aiProvider,
        IPromptAssembler promptAssembler,
        IEnumerable<ISkillToolExecutor> toolExecutors,
        ILoggerFactory loggerFactory)
        : this(aiProvider, promptAssembler, toolExecutors, null, loggerFactory)
    {
    }

    /// <summary>
    /// Legacy constructor for callers that don't supply tool executors.
    /// </summary>
    public HostedExecutionDispatcher(
        IAiProvider aiProvider,
        IPromptAssembler promptAssembler,
        StreamEventPublisher? streamEventPublisher,
        ILoggerFactory loggerFactory)
        : this(aiProvider, promptAssembler, Array.Empty<ISkillToolExecutor>(), streamEventPublisher, loggerFactory)
    {
    }

    /// <summary>
    /// Legacy constructor for callers that don't supply tool executors or a stream publisher.
    /// </summary>
    public HostedExecutionDispatcher(
        IAiProvider aiProvider,
        IPromptAssembler promptAssembler,
        ILoggerFactory loggerFactory)
        : this(aiProvider, promptAssembler, Array.Empty<ISkillToolExecutor>(), null, loggerFactory)
    {
    }

    /// <inheritdoc />
    public async Task<Message?> DispatchAsync(
        Message message,
        PromptAssemblyContext? context,
        ExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode != ExecutionMode.Hosted)
        {
            throw new SpringException($"HostedExecutionDispatcher only handles Hosted mode, got {mode}");
        }

        _logger.LogDebug("Assembling prompt for message {MessageId}.", message.Id);

        var assembly = await _promptAssembler.AssembleForToolsAsync(message, context, cancellationToken);

        // Streaming path: when a publisher is wired, stream deltas — and run the tool-use loop
        // if tools are available, so agents keep working while users watch the response unfold.
        if (_streamEventPublisher is not null)
        {
            return assembly.Tools.Count == 0
                ? await DispatchStreamingAsync(message, assembly.SystemPrompt, cancellationToken)
                : await DispatchStreamingWithToolsAsync(message, assembly, cancellationToken);
        }

        // When no tools are advertised, preserve the original single-shot behaviour so existing
        // CompleteAsync-only providers (and their tests) keep working without needing tool use.
        if (assembly.Tools.Count == 0)
        {
            _logger.LogDebug("Sending prompt to AI provider for message {MessageId}.", message.Id);
            var response = await _aiProvider.CompleteAsync(assembly.SystemPrompt, cancellationToken);
            _logger.LogDebug("Received AI response for message {MessageId}.", message.Id);
            return BuildResponseMessage(message, response);
        }

        return await DispatchWithToolsAsync(message, assembly, cancellationToken);
    }

    private async Task<Message?> DispatchWithToolsAsync(
        Message message,
        PromptAssemblyResult assembly,
        CancellationToken cancellationToken)
    {
        var turns = assembly.InitialTurns.ToList();

        for (var i = 0; i < MaxToolIterations; i++)
        {
            _logger.LogDebug("Tool-use iteration {Iteration} for message {MessageId}.", i + 1, message.Id);

            var response = await _aiProvider.CompleteWithToolsAsync(turns, assembly.Tools, cancellationToken);

            var assistantContent = BuildAssistantContent(response);
            if (assistantContent.Count > 0)
            {
                turns.Add(new ConversationTurn("assistant", assistantContent));
            }

            if (response.StopReason != "tool_use" || response.ToolCalls.Count == 0)
            {
                _logger.LogDebug(
                    "Tool-use loop completed for message {MessageId} with stop reason {StopReason}.",
                    message.Id, response.StopReason);
                return BuildResponseMessage(message, response.Text ?? string.Empty);
            }

            var resultBlocks = new List<ContentBlock>(response.ToolCalls.Count);
            foreach (var call in response.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var executor = _toolExecutors.FirstOrDefault(e => e.CanHandle(call.Name));
                ToolResult result;
                if (executor is null)
                {
                    _logger.LogWarning(
                        "No executor registered for tool '{ToolName}' (id {ToolUseId}).",
                        call.Name, call.Id);
                    result = new ToolResult(
                        call.Id,
                        $"no executor registered for tool '{call.Name}'",
                        IsError: true);
                }
                else
                {
                    try
                    {
                        result = await executor.ExecuteAsync(call, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Tool executor for '{ToolName}' (id {ToolUseId}) threw an exception.",
                            call.Name, call.Id);
                        result = new ToolResult(
                            call.Id,
                            $"tool '{call.Name}' threw an exception: {ex.Message}",
                            IsError: true);
                    }
                }

                resultBlocks.Add(new ContentBlock.ToolResultBlock(result.ToolUseId, result.Content, result.IsError));
            }

            turns.Add(new ConversationTurn("user", resultBlocks));
        }

        _logger.LogWarning(
            "Tool-use loop exceeded {Max} iterations for message {MessageId}; truncating conversation.",
            MaxToolIterations, message.Id);
        return BuildResponseMessage(message, "tool-use loop iteration cap reached; conversation truncated");
    }

    private static List<ContentBlock> BuildAssistantContent(AiResponse response)
    {
        var blocks = new List<ContentBlock>();
        if (!string.IsNullOrEmpty(response.Text))
        {
            blocks.Add(new ContentBlock.TextBlock(response.Text));
        }

        foreach (var call in response.ToolCalls)
        {
            blocks.Add(new ContentBlock.ToolUseBlock(call.Id, call.Name, call.Input));
        }

        return blocks;
    }

    private static Message BuildResponseMessage(Message source, string text)
    {
        var payload = JsonSerializer.SerializeToElement(new { text });
        return new Message(
            Guid.NewGuid(),
            source.To,
            source.From,
            MessageType.Domain,
            source.ConversationId,
            payload,
            DateTimeOffset.UtcNow);
    }

    private async Task<Message?> DispatchStreamingAsync(Message message, string prompt, CancellationToken cancellationToken)
    {
        var agentId = message.To.Path;
        var responseBuilder = new StringBuilder();

        _logger.LogDebug("Starting streaming dispatch for message {MessageId} to agent {AgentId}.",
            message.Id, agentId);

        await foreach (var streamEvent in _aiProvider.StreamCompleteAsync(prompt, cancellationToken))
        {
            await _streamEventPublisher!.PublishAsync(agentId, streamEvent, cancellationToken);

            if (streamEvent is StreamEvent.TokenDelta tokenDelta)
            {
                responseBuilder.Append(tokenDelta.Text);
            }
        }

        _logger.LogDebug("Streaming completed for message {MessageId}.", message.Id);

        return BuildResponseMessage(message, responseBuilder.ToString());
    }

    private async Task<Message?> DispatchStreamingWithToolsAsync(
        Message message,
        PromptAssemblyResult assembly,
        CancellationToken cancellationToken)
    {
        var agentId = message.To.Path;
        var turns = assembly.InitialTurns.ToList();

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            _logger.LogDebug("Streaming tool-use iteration {Iteration} for message {MessageId}.",
                iteration + 1, message.Id);

            var iterationText = new StringBuilder();
            var toolUses = new List<ToolCall>();
            string? stopReason = null;

            await foreach (var streamEvent in _aiProvider.StreamCompleteWithToolsAsync(turns, assembly.Tools, cancellationToken))
            {
                await _streamEventPublisher!.PublishAsync(agentId, streamEvent, cancellationToken);

                switch (streamEvent)
                {
                    case StreamEvent.TokenDelta tokenDelta:
                        iterationText.Append(tokenDelta.Text);
                        break;
                    case StreamEvent.ToolUseComplete toolUse:
                        toolUses.Add(new ToolCall(toolUse.ToolUseId, toolUse.ToolName, toolUse.Input));
                        break;
                    case StreamEvent.Completed completed:
                        stopReason = completed.StopReason;
                        break;
                }
            }

            var assistantBlocks = new List<ContentBlock>();
            var iterationTextString = iterationText.ToString();
            if (iterationTextString.Length > 0)
            {
                assistantBlocks.Add(new ContentBlock.TextBlock(iterationTextString));
            }

            foreach (var toolUse in toolUses)
            {
                assistantBlocks.Add(new ContentBlock.ToolUseBlock(toolUse.Id, toolUse.Name, toolUse.Input));
            }

            if (assistantBlocks.Count > 0)
            {
                turns.Add(new ConversationTurn("assistant", assistantBlocks));
            }

            if (stopReason != "tool_use" || toolUses.Count == 0)
            {
                _logger.LogDebug(
                    "Streaming tool-use loop completed for message {MessageId} with stop reason {StopReason}.",
                    message.Id, stopReason);
                return BuildResponseMessage(message, iterationTextString);
            }

            var resultBlocks = new List<ContentBlock>(toolUses.Count);
            foreach (var call in toolUses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var executor = _toolExecutors.FirstOrDefault(e => e.CanHandle(call.Name));
                ToolResult result;
                if (executor is null)
                {
                    _logger.LogWarning(
                        "No executor registered for tool '{ToolName}' (id {ToolUseId}) during streaming.",
                        call.Name, call.Id);
                    result = new ToolResult(
                        call.Id,
                        $"no executor registered for tool '{call.Name}'",
                        IsError: true);
                }
                else
                {
                    try
                    {
                        result = await executor.ExecuteAsync(call, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Tool executor for '{ToolName}' (id {ToolUseId}) threw during streaming.",
                            call.Name, call.Id);
                        result = new ToolResult(
                            call.Id,
                            $"tool '{call.Name}' threw an exception: {ex.Message}",
                            IsError: true);
                    }
                }

                resultBlocks.Add(new ContentBlock.ToolResultBlock(result.ToolUseId, result.Content, result.IsError));
            }

            turns.Add(new ConversationTurn("user", resultBlocks));
        }

        _logger.LogWarning(
            "Streaming tool-use loop exceeded {Max} iterations for message {MessageId}; truncating conversation.",
            MaxToolIterations, message.Id);
        return BuildResponseMessage(message, "tool-use loop iteration cap reached; conversation truncated");
    }
}