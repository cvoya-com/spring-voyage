// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using Cvoya.Spring.Core.Initiative;

using NSubstitute;

/// <summary>
/// Shared test helper for configuring an <see cref="IAgentInitiativeEvaluator"/>
/// NSubstitute mock with a default "act autonomously" decision so tests that
/// do not care about the initiative seam can move past it without boilerplate.
/// Tests that exercise Reactive / Proactive / Autonomous semantics override
/// the return value explicitly.
/// </summary>
internal static class InitiativeEvaluatorTestDefaults
{
    /// <summary>
    /// Applies the default "act autonomously at Attentive level" mock setup to
    /// <paramref name="evaluator"/>. The Attentive choice matches the baseline
    /// (Reactive) effective level used by most non-initiative-focused tests;
    /// the decision field is the only value the dispatch path branches on, so
    /// the level-for-logging default is benign.
    /// </summary>
    /// <param name="evaluator">The NSubstitute-backed evaluator to configure.</param>
    /// <returns>The same evaluator for fluent chaining.</returns>
    public static IAgentInitiativeEvaluator WithActAutonomouslyByDefault(
        this IAgentInitiativeEvaluator evaluator)
    {
        evaluator.EvaluateAsync(
                Arg.Any<InitiativeEvaluationContext>(),
                Arg.Any<CancellationToken>())
            .Returns(InitiativeEvaluationResult.Autonomously(InitiativeLevel.Autonomous));
        return evaluator;
    }
}