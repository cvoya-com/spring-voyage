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
    /// <param name="context">
    /// The per-invocation context (peer directory, policies, skills, prior messages,
    /// agent instructions). Passing context as a parameter keeps assemblers thread-safe
    /// across concurrent actors that share a singleton instance. When <c>null</c>, only
    /// the platform layer is rendered.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The assembled prompt string.</returns>
    Task<string> AssembleAsync(Message message, PromptAssemblyContext? context, CancellationToken cancellationToken = default);
}