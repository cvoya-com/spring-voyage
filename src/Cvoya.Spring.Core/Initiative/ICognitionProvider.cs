// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Provides the two-tier cognition model for agent initiative.
/// Tier 1 (screening) is fast and cheap; Tier 2 (reflection) invokes the full cognition loop.
/// </summary>
public interface ICognitionProvider
{
    /// <summary>
    /// Tier 1 screening: performs fast, cheap evaluation of an event against agent context.
    /// Returns a decision on whether to ignore, queue, or act immediately.
    /// </summary>
    /// <param name="context">The screening context including agent info and event details.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The screening decision.</returns>
    Task<InitiativeDecision> ScreenAsync(ScreeningContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Tier 2 full cognition loop: perceive, reflect, decide, act, learn.
    /// Invoked selectively when Tier 1 screening determines it is warranted.
    /// </summary>
    /// <param name="context">The reflection context including observations and allowed actions.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome of the reflection, including any decided action.</returns>
    Task<ReflectionOutcome> ReflectAsync(ReflectionContext context, CancellationToken cancellationToken);
}