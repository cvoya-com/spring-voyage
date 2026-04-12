// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Assembles prompts for AI model interactions from messages and context.
/// </summary>
public interface IPromptAssembler
{
    /// <summary>
    /// Assembles a prompt string from the given message and execution context.
    /// </summary>
    /// <param name="message">The message to assemble a prompt from.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The assembled prompt string.</returns>
    Task<string> AssembleAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assembles a prompt for a tool-use capable provider, producing the system prompt,
    /// the tool definitions to expose, and the initial conversation turns to seed the loop.
    /// The default implementation delegates to <see cref="AssembleAsync"/> and returns no
    /// tools or initial turns, so legacy callers keep working.
    /// </summary>
    /// <param name="message">The message to assemble a prompt from.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The assembled prompt artefacts.</returns>
    async Task<PromptAssemblyResult> AssembleForToolsAsync(Message message, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await AssembleAsync(message, cancellationToken);
        return new PromptAssemblyResult(
            systemPrompt,
            Array.Empty<ToolDefinition>(),
            Array.Empty<ConversationTurn>());
    }
}