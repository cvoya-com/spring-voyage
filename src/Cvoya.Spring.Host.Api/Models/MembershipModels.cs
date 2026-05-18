// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Collections.Generic;

using Cvoya.Spring.Core.Agents;

/// <summary>
/// Wire projection of one <c>(unit, agent)</c> membership edge with its
/// per-membership config overrides (see #160 / C2b-1). <c>Null</c> overrides
/// mean "inherit the agent's own value." The receive-path consumption of
/// these overrides lands in C2b-2 — in C2b-1 they are persisted but not
/// yet consulted at dispatch time.
/// </summary>
/// <param name="UnitId">
/// The identity-form unit address (<c>unit:id:&lt;hex&gt;</c>) this
/// membership attaches the agent to.
/// </param>
/// <param name="AgentAddress">
/// The agent's canonical 32-char no-dash hex id (matches
/// <see cref="AgentResponse.Name"/>). Suitable for use directly as a URL
/// path segment in the <c>/units/{unitId}/memberships/{agentAddress}</c>
/// surface. Per ADR-0036 the wire form is identity, never display name —
/// see #2114 for the alignment with <see cref="UnitResponse"/> and
/// <see cref="AgentResponse"/>.
/// </param>
/// <param name="AgentDisplayName">
/// The agent's human-readable display name, looked up at projection time
/// from the directory entry. May be empty when the directory entry is no
/// longer available (e.g. the agent was deleted between membership read
/// and projection); never <c>null</c>.
/// </param>
/// <param name="Member">
/// Scheme-prefixed canonical identity-form address of the member (#1060):
/// always <c>agent:&lt;hex&gt;</c> for this DTO because
/// <c>UnitMembershipResponse</c> only carries agent-scheme rows. The
/// unified <c>member</c> column lets scripts consume mixed agent/sub-unit
/// member rows from <c>spring unit members list --output json</c> without
/// branching on <c>agentAddress</c> vs <c>subUnitId</c>.
/// </param>
/// <param name="Model">Per-membership model override, or <c>null</c> to inherit.</param>
/// <param name="Specialty">Per-membership specialty override, or <c>null</c> to inherit.</param>
/// <param name="Enabled">Per-membership enabled flag; defaults to <c>true</c> on creation.</param>
/// <param name="ExecutionMode">Per-membership execution-mode override, or <c>null</c> to inherit.</param>
/// <param name="CreatedAt">UTC timestamp when the membership was first created.</param>
/// <param name="UpdatedAt">UTC timestamp when the membership was last updated.</param>
/// <param name="IsPrimary">
/// <c>true</c> when this membership is the agent's canonical parent unit.
/// Exactly one membership per agent is primary at a time; the server
/// auto-assigns on first insert and auto-promotes when the primary
/// membership is deleted. Clients cannot write this flag.
/// </param>
/// <param name="AgentHostingMode">
/// The member agent's declared hosting mode (lowercase <c>"ephemeral"</c> or
/// <c>"persistent"</c>), projected from the agent's persisted
/// <c>execution.hosting</c> field — same semantics as
/// <see cref="AgentResponse.HostingMode"/>. <c>null</c> when the agent has
/// no execution block, no hosting declaration, or the execution-store read
/// failed (fail-open). The dispatcher's platform default is
/// <c>"persistent"</c> when this value is <c>null</c>.
/// <para>
/// Projected onto the membership row so the unit Execution tab can render
/// per-member hosting without a full-tenant <c>GET /agents</c> fan-out — M
/// lookups (members only) instead of N (entire tenant).
/// </para>
/// </param>
public record UnitMembershipResponse(
    string UnitId,
    string AgentAddress,
    string AgentDisplayName,
    string Member,
    string? Model,
    string? Specialty,
    bool Enabled,
    AgentExecutionMode? ExecutionMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsPrimary,
    string? AgentHostingMode = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Expertise = null);

/// <summary>
/// Request body for <c>PUT /api/v1/units/{unitId}/memberships/{agentAddress}</c>.
/// Upserts the membership row and overwrites all override fields with the
/// supplied values. Omitting a field (or sending <c>null</c>) clears the
/// corresponding override — this is full replacement, not partial PATCH,
/// because there is no other path for an operator to express "clear this
/// override."
/// </summary>
/// <param name="Model">Per-membership model override, or <c>null</c> to inherit.</param>
/// <param name="Specialty">Per-membership specialty override, or <c>null</c> to inherit.</param>
/// <param name="Enabled">Per-membership enabled flag. <c>null</c> is treated as <c>true</c> (new membership default).</param>
/// <param name="ExecutionMode">Per-membership execution-mode override, or <c>null</c> to inherit.</param>
public record UpsertMembershipRequest(
    string? Model = null,
    string? Specialty = null,
    bool? Enabled = null,
    AgentExecutionMode? ExecutionMode = null);
