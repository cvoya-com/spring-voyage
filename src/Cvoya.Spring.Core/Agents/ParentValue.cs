// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

/// <summary>
/// A single conflicting value contributed by one of an agent's parent units
/// during multi-parent execution-config inheritance resolution.
/// </summary>
/// <remarks>
/// Per ADR-0039 §6, when an agent has more than one parent unit and any
/// execution-config field is left to inherit, the platform resolves each
/// parent's effective config. If a field diverges across parents, the
/// resolution result reports a list of <see cref="ParentValue"/> entries
/// for that field — one per contributing parent — so the operator can see
/// which parent supplied which value.
/// </remarks>
/// <param name="Source">
/// The unit id of the parent that contributed <paramref name="Value"/>.
/// </param>
/// <param name="Value">
/// The string-form value the parent's effective config resolved for the
/// conflicting field. Stringified at the resolver boundary to keep this
/// record agnostic to the per-field type (runtime id, model id, image
/// reference, etc.).
/// </param>
public sealed record ParentValue(Guid Source, string Value);