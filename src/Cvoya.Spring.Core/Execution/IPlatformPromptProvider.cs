// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Provides the platform-level prompt layer (Layer 1) containing safety constraints,
/// tool descriptions, and behavioral guidance.
/// </summary>
public interface IPlatformPromptProvider
{
    /// <summary>
    /// Returns the platform prompt text.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The platform prompt string.</returns>
    Task<string> GetPlatformPromptAsync(CancellationToken cancellationToken = default);
}