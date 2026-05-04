// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

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
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ActivateAsync(
        string packageName,
        ResolvedArtefact artefact,
        System.Guid installId,
        LocalSymbolMap symbolMap,
        CancellationToken cancellationToken = default);
}