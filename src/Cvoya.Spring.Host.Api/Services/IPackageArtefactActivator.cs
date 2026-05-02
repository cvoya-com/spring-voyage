// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Manifest;

/// <summary>
/// Activates a single resolved artefact in Phase 2 of the two-phase
/// package install (ADR-0035 decision 11). The default implementation
/// delegates to the same actor-activation path that
/// <see cref="UnitCreationService.CreateFromManifestAsync"/> uses.
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
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ActivateAsync(
        string packageName,
        ResolvedArtefact artefact,
        System.Guid installId,
        CancellationToken cancellationToken = default);
}