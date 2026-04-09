/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Provides an abstraction over AI model interactions.
/// </summary>
public interface IAiProvider
{
    /// <summary>
    /// Sends a prompt to the AI model and returns the response.
    /// </summary>
    /// <param name="prompt">The prompt to send to the AI model.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The AI model's response.</returns>
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
}
