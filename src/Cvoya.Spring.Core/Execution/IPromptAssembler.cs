// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Messaging;

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
}
