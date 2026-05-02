// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Manifest;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPackageArtefactActivator"/> implementation.
/// Delegates to <see cref="IUnitCreationService"/> for unit artefacts,
/// reusing the actor-activation path (directory registration, actor metadata
/// writes, member wiring).
/// </summary>
public class DefaultPackageArtefactActivator : IPackageArtefactActivator
{
    private readonly IUnitCreationService _unitCreationService;
    private readonly ILogger<DefaultPackageArtefactActivator> _logger;

    /// <summary>
    /// Initialises a new <see cref="DefaultPackageArtefactActivator"/>.
    /// </summary>
    public DefaultPackageArtefactActivator(
        IUnitCreationService unitCreationService,
        ILogger<DefaultPackageArtefactActivator> logger)
    {
        _unitCreationService = unitCreationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ActivateAsync(
        string packageName,
        ResolvedArtefact artefact,
        Guid installId,
        CancellationToken cancellationToken = default)
    {
        if (artefact.Content is null)
        {
            // Cross-package artefacts are already active in another installed package;
            // no activation needed.
            return;
        }

        switch (artefact.Kind)
        {
            case ArtefactKind.Unit:
                await ActivateUnitAsync(artefact, cancellationToken);
                break;

            case ArtefactKind.Agent:
                await ActivateAgentAsync(artefact, cancellationToken);
                break;

            case ArtefactKind.Skill:
            case ArtefactKind.Workflow:
                // Skills and workflows are registered via other paths that
                // read from disk at resolution time; no actor-activation step.
                _logger.LogDebug(
                    "Artefact {Kind} '{Name}' in package '{Package}' does not require actor activation.",
                    artefact.Kind, artefact.Name, packageName);
                break;

            default:
                _logger.LogWarning(
                    "Unknown artefact kind {Kind} for '{Name}' in package '{Package}'; skipping activation.",
                    artefact.Kind, artefact.Name, packageName);
                break;
        }
    }

    private async Task ActivateUnitAsync(ResolvedArtefact artefact, CancellationToken ct)
    {
        var manifest = ManifestParser.Parse(artefact.Content!);
        var overrides = new UnitCreationOverrides(IsTopLevel: true);
        await _unitCreationService.CreateFromManifestAsync(manifest, overrides, ct);
    }

    private Task ActivateAgentAsync(ResolvedArtefact artefact, CancellationToken ct)
    {
        // Agent manifests are activated via the same directory-registration
        // path as units, but the agent-specific create path is not part of
        // IUnitCreationService. For now, log a notice and no-op — the agent
        // artefact's staging row will remain in Staging/Active depending on
        // whether any unit in the package references it (which will trigger
        // auto-registration via UnitCreationService.CreateCoreAsync's
        // directory auto-register path when the unit is activated).
        // Full agent-level activation is tracked in #1559 (the HTTP endpoint
        // layer that wires the agent-create path into the install pipeline).
        _logger.LogDebug(
            "Agent artefact '{Name}': activated implicitly via unit member auto-registration.",
            artefact.Name);
        return Task.CompletedTask;
    }
}