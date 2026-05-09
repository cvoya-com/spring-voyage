// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// The outcome of resolving an agent's effective execution config across its
/// own values and the resolved configs of its parent unit(s), per ADR-0039 §6.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="ConflictingFields"/> is empty, <see cref="Effective"/>
/// holds the agent's resolved configuration: agent-owned values win, and
/// any inherited field is filled from the (consistent) parent set.
/// </para>
/// <para>
/// When <see cref="ConflictingFields"/> is non-empty, the agent inherited a
/// field whose parents disagreed. Endpoints reject the create / update with a
/// 422 carrying the conflict map; the operator must either remove the
/// conflicting parent or set the field explicitly on the agent.
/// </para>
/// </remarks>
/// <param name="Effective">
/// The resolved effective execution config the platform would persist if
/// <see cref="ConflictingFields"/> were empty. Callers must not consume this
/// value when conflicts are present.
/// </param>
/// <param name="ConflictingFields">
/// A map from the diverging field name (e.g. <c>runtime</c>, <c>model</c>,
/// <c>image</c>, <c>hosting</c>) to the per-parent values that diverged. Empty
/// when resolution succeeded.
/// </param>
public sealed record InheritanceResolution(
    AgentExecutionConfig Effective,
    IReadOnlyDictionary<string, IReadOnlyList<ParentValue>> ConflictingFields);
