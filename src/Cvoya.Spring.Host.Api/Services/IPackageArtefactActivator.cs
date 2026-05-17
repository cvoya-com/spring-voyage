// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Manifest;

/// <summary>
/// Activates a single resolved artefact in Phase 2 of the two-phase
/// package install (ADR-0035 decision 11). The default implementation
/// delegates to <see cref="IUnitCreationService"/> for unit artefacts.
/// Test harnesses substitute a recording or failing implementation.
/// </summary>
public interface IPackageArtefactActivator
{
    /// <summary>
    /// Activates the artefact described by <paramref name="artefact"/>
    /// within the context of a package install. Called after Phase-1 rows
    /// have been committed with <c>state = staging</c>.
    /// </summary>
    /// <param name="packageName">The owning package name.</param>
    /// <param name="artefact">The resolved artefact to activate.</param>
    /// <param name="installId">The shared install batch identifier.</param>
    /// <param name="symbolMap">
    /// The per-package local-symbol → Guid map minted in Phase 1 (#1629 PR7).
    /// The activator uses this map to look up the artefact's pre-allocated
    /// identity (so the directory entry it writes lines up with the
    /// staging row Phase 1 already committed) and to resolve any
    /// member-list entry references against peer artefacts in the same
    /// package.
    /// </param>
    /// <param name="connectorBindings">
    /// Resolved connector bindings for this artefact when it is a unit
    /// (#1671). Keyed by connector slug. Empty when the unit declares no
    /// connectors. Forwarded to <see cref="IUnitCreationService.CreateFromManifestAsync"/>
    /// so the unit-creation pipeline can write each binding into the
    /// existing per-unit store atomically with the unit itself.
    /// </param>
    /// <param name="executionDefaults">
    /// Resolved execution defaults for this artefact when it is a unit
    /// (#1679). Carries the field-wise merge of the package's
    /// <c>execution:</c> block (when declared and the unit is eligible
    /// to inherit) and the unit's own block. The activator overlays
    /// these defaults onto the parsed
    /// <see cref="UnitManifest.Execution"/> before forwarding to the
    /// unit-creation service so the persisted execution block reflects
    /// the merged result rather than the raw member-level YAML.
    /// </param>
    /// <param name="displayNameOverride">
    /// Optional display-name override (#2310). When non-null, replaces
    /// the artefact's <c>name:</c> field for display purposes — the
    /// directory entry the activator registers and the persisted
    /// <c>unit_definitions</c> / <c>agent_definitions</c> row carry this
    /// name instead. Used by the install pipeline so the same package
    /// can be installed multiple times without producing confusingly
    /// identical display names. Caller-side validation (rejection when
    /// the package ships multiple top-level activatables) lives in
    /// <see cref="IPackageInstallService"/>; the activator just trusts
    /// what it receives.
    /// </param>
    /// <param name="inheritedAgentHosting">
    /// Optional inherited <c>execution.hosting</c> literal for an Agent
    /// artefact (issue #2436). Carries the hosting value the install
    /// pipeline resolved from the agent's containing unit (or the agent
    /// template chain) when the agent itself did not declare one — the
    /// precedence chain is <i>agent &gt; template &gt; unit &gt; default
    /// (`persistent`)</i>. Ignored for non-Agent artefacts. The activator
    /// writes this literal onto the agent's persisted definition JSON
    /// under <c>execution.hosting</c> only when the agent's own
    /// declarative shape (its YAML's <c>execution.hosting</c>) is absent.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ActivateAsync(
        string packageName,
        ResolvedArtefact artefact,
        System.Guid installId,
        LocalSymbolMap symbolMap,
        IReadOnlyDictionary<string, ConnectorBinding>? connectorBindings = null,
        ResolvedExecutionDefaults? executionDefaults = null,
        string? displayNameOverride = null,
        string? inheritedAgentHosting = null,
        CancellationToken cancellationToken = default);
}
