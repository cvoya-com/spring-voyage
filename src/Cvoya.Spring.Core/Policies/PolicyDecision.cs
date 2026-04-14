// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Outcome of a <see cref="IUnitPolicyEnforcer"/> evaluation. A decision is
/// always "allow unless denied" — if every unit containing the agent either
/// has no relevant policy or explicitly allows the action, the decision is
/// <see cref="Allowed"/>. The first unit that denies short-circuits the
/// evaluation and its identity is recorded on <see cref="DenyingUnitId"/>
/// so operators can trace the rejection.
/// </summary>
/// <param name="IsAllowed">Whether the action is permitted.</param>
/// <param name="Reason">
/// Human-readable reason. <c>null</c> when <see cref="IsAllowed"/> is <c>true</c>.
/// </param>
/// <param name="DenyingUnitId">
/// The unit whose policy blocked the action, or <c>null</c> when the action
/// was allowed. Useful for audit trails and tool-error payloads.
/// </param>
public readonly record struct PolicyDecision(
    bool IsAllowed,
    string? Reason = null,
    string? DenyingUnitId = null)
{
    /// <summary>
    /// Canonical "allowed" decision. Singleton-style factory to keep call
    /// sites readable and avoid ad-hoc <c>new PolicyDecision(true)</c>.
    /// </summary>
    public static PolicyDecision Allowed { get; } = new(true);

    /// <summary>
    /// Creates a deny decision with the supplied reason and denying unit id.
    /// </summary>
    /// <param name="reason">Human-readable reason.</param>
    /// <param name="unitId">The unit whose policy blocked the action.</param>
    public static PolicyDecision Deny(string reason, string? unitId = null) =>
        new(false, reason, unitId);
}