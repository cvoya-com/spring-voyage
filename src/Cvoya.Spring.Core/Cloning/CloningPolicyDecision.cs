// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Cloning;

/// <summary>
/// Outcome of an <see cref="IAgentCloningPolicyEnforcer"/> evaluation
/// against a clone request. The record is an expected-failure shape —
/// a denied request returns a <see cref="CloningPolicyDecision"/> with
/// <see cref="Allowed"/> set to <c>false</c>; the enforcer does not throw
/// so a policy-store outage can be surfaced as a warning without dropping
/// the caller's context.
/// </summary>
/// <param name="Allowed">
/// <c>true</c> when the request is accepted. <c>false</c> when any
/// evaluated dimension denies.
/// </param>
/// <param name="DeniedDimension">
/// The first-denying dimension name when <see cref="Allowed"/> is
/// <c>false</c> — one of "policy", "attachment", "max-clones",
/// "max-depth", "budget", or "boundary". <c>null</c> when allowed.
/// </param>
/// <param name="Reason">
/// Operator-facing explanation when <see cref="Allowed"/> is
/// <c>false</c>. <c>null</c> when allowed.
/// </param>
/// <param name="ResolvedMaxClones">
/// The effective <c>MaxClones</c> value the validation activity should
/// enforce, after merging agent + tenant-scoped policies. <c>null</c>
/// when no scope sets the cap.
/// </param>
/// <param name="ResolvedBudget">
/// The effective per-clone budget the validation activity should enforce,
/// after merging agent + tenant-scoped policies. <c>null</c> when no
/// scope sets a budget.
/// </param>
public record CloningPolicyDecision(
    bool Allowed,
    string? DeniedDimension = null,
    string? Reason = null,
    int? ResolvedMaxClones = null,
    decimal? ResolvedBudget = null)
{
    /// <summary>An allow decision with no resolved overrides.</summary>
    public static CloningPolicyDecision AllowedUnconstrained { get; } = new(true);

    /// <summary>Factory for a deny decision.</summary>
    /// <param name="dimension">Name of the denying dimension.</param>
    /// <param name="reason">Operator-facing explanation.</param>
    public static CloningPolicyDecision Deny(string dimension, string reason) =>
        new(false, dimension, reason);
}