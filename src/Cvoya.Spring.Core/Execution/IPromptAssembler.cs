/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
