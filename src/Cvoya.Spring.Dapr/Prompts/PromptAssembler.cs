// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Assembles prompts by composing four layers: platform instructions, unit context,
/// conversation context, and agent instructions.
/// </summary>
public class PromptAssembler(
    IPlatformPromptProvider platformPromptProvider,
    UnitContextBuilder unitContextBuilder,
    ConversationContextBuilder conversationContextBuilder,
    ILoggerFactory loggerFactory) : IPromptAssembler
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PromptAssembler>();

    /// <inheritdoc />
    public async Task<string> AssembleAsync(
        Message message,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Assembling prompt for message {MessageId}.", message.Id);

        var builder = new StringBuilder();

        // Layer 1: Platform instructions
        var platform = await platformPromptProvider.GetPlatformPromptAsync(cancellationToken);
        builder.AppendLine("## Platform Instructions");
        builder.AppendLine(platform);
        builder.AppendLine();

        if (context is not null)
        {
            // Layer 2: Unit context
            var unitContext = unitContextBuilder.Build(
                context.Members,
                context.Policies,
                context.Skills);

            if (!string.IsNullOrWhiteSpace(unitContext))
            {
                builder.AppendLine("## Unit Context");
                builder.AppendLine(unitContext);
                builder.AppendLine();
            }

            // Layer 3: Conversation context
            var conversationContext = conversationContextBuilder.Build(
                context.PriorMessages,
                context.LastCheckpoint);

            if (!string.IsNullOrWhiteSpace(conversationContext))
            {
                builder.AppendLine("## Conversation Context");
                builder.AppendLine(conversationContext);
                builder.AppendLine();
            }

            // Layer 4: Agent instructions
            if (!string.IsNullOrWhiteSpace(context.AgentInstructions))
            {
                builder.AppendLine("## Agent Instructions");
                builder.AppendLine(context.AgentInstructions);
                builder.AppendLine();
            }
        }

        _logger.LogDebug("Prompt assembly complete for message {MessageId}.", message.Id);
        return builder.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public async Task<PromptAssemblyResult> AssembleForToolsAsync(
        Message message,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = await AssembleAsync(message, context, cancellationToken);

        var tools = context?.GetAllTools() ?? Array.Empty<ToolDefinition>();

        var userText = ExtractUserText(message);
        IReadOnlyList<ConversationTurn> initialTurns =
        [
            new ConversationTurn("user", [new ContentBlock.TextBlock(userText)])
        ];

        return new PromptAssemblyResult(systemPrompt, tools, initialTurns);
    }

    private static string ExtractUserText(Message message)
    {
        if (message.Payload.ValueKind == JsonValueKind.Object &&
            message.Payload.TryGetProperty("text", out var textEl) &&
            textEl.ValueKind == JsonValueKind.String)
        {
            return textEl.GetString() ?? string.Empty;
        }

        return message.Payload.ValueKind == JsonValueKind.Undefined
            ? string.Empty
            : message.Payload.GetRawText();
    }
}