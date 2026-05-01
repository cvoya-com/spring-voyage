// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Per-membership configuration for an agent belonging to a unit.
/// Replaces the prior 1:N <c>AgentMetadata.ParentUnit</c> pointer with an
/// M:N membership row (see #160). The override fields mirror the shape of
/// <see cref="AgentMetadata"/>: <c>null</c> means "inherit the agent's own
/// value" at dispatch time; a non-<c>null</c> value overrides it for
/// messages flowing through this particular unit.
/// </summary>
/// <param name="UnitId">
/// The stable UUID identity of the unit this membership attaches the agent
/// to (#1492). Matches the unit's <c>ActorId</c> so a delete + recreate of
/// a unit with the same slug does not inherit stale membership rows.
/// </param>
/// <param name="AgentId">
/// The stable UUID identity of the agent (#1492). Matches the agent's
/// <c>ActorId</c>; renamed from the prior <c>AgentAddress</c> because
/// "address" implied a slug-shaped URI string.
/// </param>
/// <param name="Model">Per-membership model override, or <c>null</c> to inherit the agent's own <c>Model</c>.</param>
/// <param name="Specialty">Per-membership specialty override, or <c>null</c> to inherit.</param>
/// <param name="Enabled">Per-membership enabled flag. Defaults to <c>true</c> on insert. When <c>false</c>, this unit's orchestration strategy skips the agent even if the agent itself is enabled.</param>
/// <param name="ExecutionMode">Per-membership execution-mode override, or <c>null</c> to inherit.</param>
/// <param name="CreatedAt">UTC timestamp when the membership was first created.</param>
/// <param name="UpdatedAt">UTC timestamp when the membership was last updated.</param>
/// <param name="IsPrimary">
/// Marks this membership as the agent's primary parent unit. Exactly one
/// membership per agent carries <c>IsPrimary = true</c> at any time; the
/// repository auto-assigns on first insert and auto-promotes when the
/// primary row is deleted. Consumed by the tenant-tree endpoint to flag
/// the canonical surface for multi-parent agents (§3 of the v2 plan).
/// </param>
public record UnitMembership(
    Guid UnitId,
    Guid AgentId,
    string? Model = null,
    string? Specialty = null,
    bool Enabled = true,
    AgentExecutionMode? ExecutionMode = null,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset UpdatedAt = default,
    bool IsPrimary = false);