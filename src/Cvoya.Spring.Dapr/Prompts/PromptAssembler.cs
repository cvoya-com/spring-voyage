// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Assembles prompts by composing four layers: platform instructions, unit context,
/// conversation context, and agent instructions. The output is the system-prompt text
/// handed to the external agent runtime by <see cref="IExecutionDispatcher"/>.
/// Stateless and safe to share across concurrent actors — all per-invocation state is
/// passed through <see cref="AssembleAsync"/>.
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
                context.Skills,
                context.SkillBundles);

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
}