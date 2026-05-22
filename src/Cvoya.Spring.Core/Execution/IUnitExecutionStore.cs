// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Read/write seam for the manifest-persisted unit <c>execution:</c>
/// block (#601 / #603 / #409 — "B-wide" shape). Both the manifest-apply
/// path (<c>UnitCreationService</c>) and the dedicated HTTP surface
/// (<c>PUT /api/v1/units/{id}/execution</c>) write through this
/// interface so the two entry points cannot drift on persistence shape
/// or validation semantics.
/// </summary>
/// <remarks>
/// <para>
/// Implementations persist into the same
/// <c>UnitDefinitions.Definition</c> JSON document the agent-definition
/// provider reads at dispatch time. The block holds three fields —
/// <c>runtime</c>, <c>model{provider, id}</c>, <c>image</c> — and
/// serves as the fallback defaults for member
/// agents per the <i>agent → unit → fail-clean</i> resolution chain
/// documented in <c>docs/architecture/units.md</c>.
/// </para>
/// <para>
/// Each field is independently clearable. A call to
/// <see cref="SetAsync"/> with <c>null</c> fields leaves the other
/// properties on the persisted JSON untouched; pass an all-null
/// <see cref="UnitExecutionDefaults"/> (or call <see cref="ClearAsync"/>)
/// to strip the block entirely.
/// </para>
/// </remarks>
public interface IUnitExecutionStore
{
    /// <summary>
    /// Returns the unit's persisted execution defaults or <c>null</c> when
    /// no block has been declared. Never throws on a missing definition
    /// row — a unit whose manifest omitted the block returns <c>null</c>
    /// so callers can surface the "no defaults" state without branching
    /// on 404.
    /// </summary>
    Task<UnitExecutionDefaults?> GetAsync(
        string unitId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the unit's execution defaults in place. An all-null
    /// <paramref name="defaults"/> strips the block; otherwise every
    /// non-null field replaces the corresponding slot, and null fields
    /// leave the existing persisted value alone (partial update).
    /// Implementations must preserve every other property on the
    /// Definition document (expertise, instructions).
    /// </summary>
    Task SetAsync(
        string unitId,
        UnitExecutionDefaults defaults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Strips the entire execution block from the unit's persisted
    /// definition document. Idempotent — clearing a unit that never had
    /// an execution block declared is a no-op.
    /// </summary>
    Task ClearAsync(
        string unitId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// View of a unit's execution defaults. Per the ADR-0038 amendment
/// (#2634) the block carries exactly <c>(runtime, model{provider, id},
/// image)</c>. Each field is independently nullable — a unit can declare
/// any subset.
/// </summary>
/// <remarks>
/// ADR-0038: the execution tool is derived 1:1 from <see cref="Runtime"/>
/// (the runtime registry id) via the catalogue runtime's
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/> field.
/// </remarks>
/// <param name="Image">Default container image reference.</param>
/// <param name="Model">Default structured <c>{provider, id}</c> model selector.</param>
/// <param name="Runtime">
/// Agent runtime registry id — sourced from the unit / agent manifest's
/// <c>ai.runtime</c> field. Matches an
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Id"/> entry in the
/// runtime catalogue (e.g. <c>claude-code</c>, <c>codex</c>,
/// <c>spring-voyage</c>). The validation scheduler reads this slot when
/// composing the workflow input.
/// </param>
public record UnitExecutionDefaults(
    string? Image = null,
    Cvoya.Spring.Core.Catalog.Model? Model = null,
    string? Runtime = null)
{
    /// <summary>True when every field is null / whitespace.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && Model is null
        && string.IsNullOrWhiteSpace(Runtime);
}
