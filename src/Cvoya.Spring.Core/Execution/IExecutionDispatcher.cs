// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Dispatches work to an external agent runtime (e.g., a container running a
/// configured AI agent tool such as Claude Code or Codex). Spring Voyage does
/// not implement its own agent loop — the dispatcher is a thin wrapper over the
/// process/container spawner.
/// </summary>
public interface IExecutionDispatcher
{
    /// <summary>
    /// Dispatches a message for execution by an external agent runtime.
    /// </summary>
    /// <param name="message">The message containing the work to dispatch.</param>
    /// <param name="context">
    /// The prompt-assembly context (unit members, policies, skills, prior messages,
    /// agent instructions) the caller has already assembled. May be <c>null</c>
    /// when the dispatcher should render only the platform prompt layer.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message to route back to the sender.</returns>
    Task<Message?> DispatchAsync(
        Message message,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default);
}