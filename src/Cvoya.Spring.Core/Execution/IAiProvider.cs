// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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