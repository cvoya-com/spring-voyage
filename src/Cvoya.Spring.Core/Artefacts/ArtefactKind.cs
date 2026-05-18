// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Artefacts;

using Cvoya.Spring.Core.Artefacts;

/// <summary>
/// The kind of artefact a reference targets — used both for package-manifest
/// reference resolution (see <c>Cvoya.Spring.Manifest.ArtefactReference</c>)
/// and for lifecycle infrastructure that routes by artefact kind
/// (see <c>Cvoya.Spring.Core.Lifecycle.LifecycleStatus</c> and the shared
/// validation workflow). #2364 promoted this from <c>Cvoya.Spring.Manifest</c>
/// to the Core layer so the lifecycle pipeline (which lives below Manifest in
/// the dependency graph) can reference it without a cycle.
/// </summary>
/// <remarks>
/// Only <see cref="Unit"/> and <see cref="Agent"/> have container lifecycles;
/// <see cref="Skill"/>, <see cref="UnitTemplate"/>, <see cref="AgentTemplate"/>,
/// and <see cref="HumanTemplate"/> are package-resolution-only values that the
/// lifecycle scheduler rejects with an exception. ADR-0045 §2 drops the
/// <c>Workflow</c> value from this enum — connector bindings survive via
/// <c>requires:</c> on consumer artefacts (ADR-0037 §3), but the shipped
/// <c>Workflow</c> / <c>Connector</c> artefact types are gone.
/// </remarks>
public enum ArtefactKind
{
    /// <summary>Resolves to <c>./units/&lt;name&gt;/package.yaml</c>; has a container lifecycle.</summary>
    Unit,

    /// <summary>Resolves to <c>./agents/&lt;name&gt;/package.yaml</c>; has a container lifecycle.</summary>
    Agent,

    /// <summary>Resolves to <c>./skills/&lt;name&gt;/</c>; no container lifecycle.</summary>
    Skill,

    /// <summary>
    /// ADR-0045 §4: declarative human template under
    /// <c>./templates/&lt;name&gt;/package.yaml</c> with <c>kind: HumanTemplate</c>.
    /// Stamped via <c>- human: { from: &lt;template-name&gt; }</c>. No container
    /// lifecycle; the template is materialised into a fresh <c>HumanEntity</c>
    /// row at install time.
    /// </summary>
    HumanTemplate,
}
