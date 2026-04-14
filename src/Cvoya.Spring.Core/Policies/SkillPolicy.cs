// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Restricts which skills (tools) agents in a unit may invoke. First concrete
/// instance of the <see cref="UnitPolicy"/> framework — see #163.
/// </summary>
/// <remarks>
/// <para>
/// Evaluation order: a tool name that appears in <paramref name="Blocked"/> is
/// always denied regardless of <paramref name="Allowed"/>. When
/// <paramref name="Allowed"/> is non-<c>null</c>, it acts as a whitelist — any
/// tool not in the list is denied. When <paramref name="Allowed"/> is
/// <c>null</c>, all tools are allowed except those in <paramref name="Blocked"/>.
/// </para>
/// <para>
/// Matching is case-insensitive. The empty-list / whitelist distinction is
/// deliberate: <c>Allowed: []</c> means "no tools may run", while
/// <c>Allowed: null</c> means "no whitelist constraint".
/// </para>
/// </remarks>
/// <param name="Allowed">
/// Optional whitelist of tool names. <c>null</c> means "no whitelist — every
/// tool not in <paramref name="Blocked"/> is allowed". A non-<c>null</c> list
/// restricts invocations to its members.
/// </param>
/// <param name="Blocked">
/// Optional blacklist of tool names. Matching entries are always denied and
/// take precedence over <paramref name="Allowed"/>.
/// </param>
public record SkillPolicy(
    IReadOnlyList<string>? Allowed = null,
    IReadOnlyList<string>? Blocked = null);