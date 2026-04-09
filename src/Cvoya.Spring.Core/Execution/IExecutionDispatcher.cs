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
/// Dispatches work to the appropriate execution environment based on the execution mode.
/// </summary>
public interface IExecutionDispatcher
{
    /// <summary>
    /// Dispatches a message for execution in the specified mode.
    /// </summary>
    /// <param name="message">The message containing the work to dispatch.</param>
    /// <param name="mode">The execution mode determining where the work runs.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message.</returns>
    Task<Message?> DispatchAsync(Message message, ExecutionMode mode, CancellationToken cancellationToken = default);
}
