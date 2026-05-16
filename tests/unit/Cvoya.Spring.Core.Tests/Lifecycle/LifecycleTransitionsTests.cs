// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Lifecycle;

using Cvoya.Spring.Core.Lifecycle;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the shared <see cref="LifecycleTransitions.IsValidTransition"/>
/// table — same arrows that <c>UnitActor</c> previously enforced inline
/// (#944 / #939 / T-02), now shared with <c>AgentActor</c> after #2364.
/// </summary>
/// <remarks>
/// Tests the full Cartesian product (7 × 7 = 49 pairs) by enumerating
/// the allowed set explicitly and asserting every other pair is rejected.
/// Adding a new transition arrow must update the allowed set here, which
/// makes the change visible at PR review.
/// </remarks>
public class LifecycleTransitionsTests
{
    /// <summary>
    /// The complete set of allowed (from, to) edges. Order here mirrors
    /// the implementation in <c>LifecycleTransitions.IsValidTransition</c>
    /// so the two stay in sync via review diff.
    /// </summary>
    private static readonly (LifecycleStatus From, LifecycleStatus To)[] AllowedEdges =
    {
        // Core operational arrows
        (LifecycleStatus.Draft, LifecycleStatus.Stopped),
        (LifecycleStatus.Stopped, LifecycleStatus.Starting),
        (LifecycleStatus.Starting, LifecycleStatus.Running),
        (LifecycleStatus.Starting, LifecycleStatus.Error),
        (LifecycleStatus.Running, LifecycleStatus.Stopping),
        (LifecycleStatus.Stopping, LifecycleStatus.Stopped),
        (LifecycleStatus.Stopping, LifecycleStatus.Error),
        (LifecycleStatus.Error, LifecycleStatus.Stopped),
        // Validation edges (#944 / T-02 / #939)
        (LifecycleStatus.Draft, LifecycleStatus.Validating),
        (LifecycleStatus.Validating, LifecycleStatus.Stopped),
        (LifecycleStatus.Validating, LifecycleStatus.Error),
        (LifecycleStatus.Error, LifecycleStatus.Validating),
        (LifecycleStatus.Stopped, LifecycleStatus.Validating),
    };

    public static IEnumerable<object[]> AllowedCases =>
        AllowedEdges.Select(e => new object[] { e.From, e.To });

    public static IEnumerable<object[]> RejectedCases
    {
        get
        {
            var allowedSet = AllowedEdges.ToHashSet();
            foreach (var from in Enum.GetValues<LifecycleStatus>())
            {
                foreach (var to in Enum.GetValues<LifecycleStatus>())
                {
                    if (!allowedSet.Contains((from, to)))
                    {
                        yield return new object[] { from, to };
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllowedCases))]
    public void IsValidTransition_AllowedEdge_ReturnsTrue(LifecycleStatus from, LifecycleStatus to)
        => LifecycleTransitions.IsValidTransition(from, to).ShouldBeTrue(
            $"{from} → {to} must be allowed");

    [Theory]
    [MemberData(nameof(RejectedCases))]
    public void IsValidTransition_DisallowedEdge_ReturnsFalse(LifecycleStatus from, LifecycleStatus to)
        => LifecycleTransitions.IsValidTransition(from, to).ShouldBeFalse(
            $"{from} → {to} must be rejected");

    [Theory]
    [InlineData(LifecycleStatus.Draft)]
    [InlineData(LifecycleStatus.Stopped)]
    [InlineData(LifecycleStatus.Starting)]
    [InlineData(LifecycleStatus.Running)]
    [InlineData(LifecycleStatus.Stopping)]
    [InlineData(LifecycleStatus.Error)]
    [InlineData(LifecycleStatus.Validating)]
    public void IsValidTransition_SelfTransition_AlwaysRejected(LifecycleStatus state)
        => LifecycleTransitions.IsValidTransition(state, state).ShouldBeFalse(
            $"self-transition {state} → {state} must be rejected");

    /// <summary>
    /// Draft → Starting is intentionally absent (#939). Artefacts must pass
    /// through Validating before they can start; pin the rejection so a
    /// future "performance" shortcut doesn't reintroduce the bug.
    /// </summary>
    [Fact]
    public void IsValidTransition_DraftToStarting_IsRejected()
        => LifecycleTransitions.IsValidTransition(LifecycleStatus.Draft, LifecycleStatus.Starting)
            .ShouldBeFalse();

    /// <summary>
    /// Running → Validating is intentionally absent. To revalidate a
    /// running artefact the operator must first Stop it.
    /// </summary>
    [Fact]
    public void IsValidTransition_RunningToValidating_IsRejected()
        => LifecycleTransitions.IsValidTransition(LifecycleStatus.Running, LifecycleStatus.Validating)
            .ShouldBeFalse();
}
