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
/// Execution dispatcher that handles hosted mode by assembling a prompt
/// and calling an AI provider in-process.
/// </summary>
public class HostedExecutionDispatcher(
    IAiProvider aiProvider,
    IPromptAssembler promptAssembler,
    ILoggerFactory loggerFactory) : IExecutionDispatcher
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<HostedExecutionDispatcher>();

    /// <inheritdoc />
    public async Task<Message?> DispatchAsync(Message message, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        if (mode != ExecutionMode.Hosted)
        {
            throw new SpringException($"HostedExecutionDispatcher only handles Hosted mode, got {mode}");
        }

        _logger.LogDebug("Assembling prompt for message {MessageId}.", message.Id);
        var prompt = await promptAssembler.AssembleAsync(message, cancellationToken);

        _logger.LogDebug("Sending prompt to AI provider for message {MessageId}.", message.Id);
        var response = await aiProvider.CompleteAsync(prompt, cancellationToken);

        _logger.LogDebug("Received AI response for message {MessageId}.", message.Id);

        var responsePayload = JsonSerializer.SerializeToElement(new { text = response });

        return new Message(
            Guid.NewGuid(),
            message.To,
            message.From,
            MessageType.Domain,
            message.ConversationId,
            responsePayload,
            DateTimeOffset.UtcNow);
    }
}
