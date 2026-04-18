// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Manifest;

/// <summary>
/// Projects a manifest-layer <see cref="BoundaryManifest"/> onto the core
/// <see cref="UnitBoundary"/> record consumed by
/// <see cref="IUnitBoundaryStore"/>. Keeping this in one place ensures the
/// <c>spring apply</c> path and any other server-side consumer that reads
/// <see cref="UnitManifest.Boundary"/> share the same tolerance rules —
/// unknown level strings resolve to <c>null</c>, synthesis entries with no
/// name are dropped, and an all-empty block maps to <see cref="UnitBoundary.Empty"/>.
/// </summary>
/// <remarks>
/// The CLI does not depend on this class — it ships its own manifest →
/// <c>UnitBoundaryResponse</c> projection so it can stay on the Kiota client
/// types without a backward reference to <c>Cvoya.Spring.Host.Api</c>. The
/// two projections are intentionally parallel; the integration tests in
/// <c>Cvoya.Spring.Host.Api.Tests</c> cover server-side round trips and the
/// CLI tests cover wire-shape equivalence.
/// </remarks>
internal static class ManifestBoundaryMapper
{
    public static UnitBoundary ToCore(BoundaryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var opacities = manifest.Opacities?
            .Where(r => r is not null)
            .Select(r => new BoundaryOpacityRule(r!.DomainPattern, r.OriginPattern))
            .ToList();

        var projections = manifest.Projections?
            .Where(r => r is not null)
            .Select(r => new BoundaryProjectionRule(
                r!.DomainPattern,
                r.OriginPattern,
                r.RenameTo,
                r.Retag,
                ParseLevel(r.OverrideLevel)))
            .ToList();

        // Synthesis entries require a non-blank name — matches the HTTP DTO
        // and CLI behaviour, so a misspelled entry never fabricates an
        // empty team capability.
        var syntheses = manifest.Syntheses?
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r!.Name))
            .Select(r => new BoundarySynthesisRule(
                r!.Name!,
                r.DomainPattern,
                r.OriginPattern,
                r.Description,
                ParseLevel(r.Level)))
            .ToList();

        return new UnitBoundary(
            Opacities: opacities is { Count: > 0 } ? opacities : null,
            Projections: projections is { Count: > 0 } ? projections : null,
            Syntheses: syntheses is { Count: > 0 } ? syntheses : null);
    }

    private static ExpertiseLevel? ParseLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Enum.TryParse<ExpertiseLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }
}