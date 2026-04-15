// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Policies;

using NSubstitute;

/// <summary>
/// Shared test helper that wires up an <see cref="IUnitPolicyEnforcer"/>
/// NSubstitute mock with every dimension returning an Allow decision by
/// default. Tests that need a denial override the specific method they care
/// about; tests that simply want the enforcer out of the way can call this
/// once and move on.
/// </summary>
internal static class UnitPolicyEnforcerTestDefaults
{
    /// <summary>
    /// Applies the allow-by-default mock setup to <paramref name="enforcer"/>.
    /// </summary>
    /// <param name="enforcer">The NSubstitute-backed enforcer to configure.</param>
    /// <returns>The same enforcer for fluent chaining.</returns>
    public static IUnitPolicyEnforcer WithAllowByDefault(this IUnitPolicyEnforcer enforcer)
    {
        enforcer.EvaluateSkillInvocationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        enforcer.EvaluateModelAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        enforcer.EvaluateCostAsync(
                Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        enforcer.EvaluateExecutionModeAsync(
                Arg.Any<string>(), Arg.Any<AgentExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        enforcer.ResolveExecutionModeAsync(
                Arg.Any<string>(), Arg.Any<AgentExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(ci => ExecutionModeResolution.AllowAsIs(ci.ArgAt<AgentExecutionMode>(1)));
        enforcer.EvaluateInitiativeActionAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        return enforcer;
    }
}