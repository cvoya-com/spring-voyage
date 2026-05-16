// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Units;

/// <summary>
/// In-memory test double for <see cref="IUnitLiveConfigStore"/>. Lets
/// unit tests exercise the EF-backed unit live-config / boundary /
/// inheritance / own-expertise surface without standing up a Postgres
/// / Testcontainer. Cross-restart behaviour is covered by the
/// integration tests with a real <c>SpringDbContext</c>.
/// </summary>
public class InMemoryUnitLiveConfigStore : IUnitLiveConfigStore
{
    private readonly ConcurrentDictionary<Guid, UnitMetadata> _metadata = new();
    private readonly ConcurrentDictionary<Guid, UnitBoundary> _boundary = new();
    private readonly ConcurrentDictionary<Guid, UnitPermissionInheritance> _inheritance = new();
    private readonly ConcurrentDictionary<Guid, ExpertiseDomain[]> _expertise = new();
    private readonly ConcurrentDictionary<Guid, bool> _expertiseInitialised = new();

    public Task<UnitMetadata> GetMetadataAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            _metadata.TryGetValue(unitId, out var v) ? v : new UnitMetadata(null, null, null, null));
    }

    public Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid unitId, UnitMetadata metadata, CancellationToken cancellationToken = default)
    {
        var written = new List<string>();
        var existing = _metadata.TryGetValue(unitId, out var v)
            ? v
            : new UnitMetadata(null, null, null, null);

        if (metadata.Model is not null)
        {
            existing = existing with { Model = metadata.Model };
            written.Add(nameof(metadata.Model));
        }
        if (metadata.Color is not null)
        {
            existing = existing with { Color = metadata.Color };
            written.Add(nameof(metadata.Color));
        }
        if (metadata.Provider is not null)
        {
            existing = existing with { Provider = metadata.Provider };
            written.Add(nameof(metadata.Provider));
        }
        if (metadata.Hosting is not null)
        {
            existing = existing with { Hosting = metadata.Hosting };
            written.Add(nameof(metadata.Hosting));
        }
        // #2341: parity slots — keep this double aligned with UnitLiveConfigRepository.
        if (metadata.Specialty is not null)
        {
            existing = existing with { Specialty = metadata.Specialty };
            written.Add(nameof(metadata.Specialty));
        }
        if (metadata.Enabled is not null)
        {
            existing = existing with { Enabled = metadata.Enabled };
            written.Add(nameof(metadata.Enabled));
        }
        if (metadata.ExecutionMode is not null)
        {
            existing = existing with { ExecutionMode = metadata.ExecutionMode };
            written.Add(nameof(metadata.ExecutionMode));
        }

        if (written.Count > 0)
        {
            _metadata[unitId] = existing;
        }
        return Task.FromResult<IReadOnlyList<string>>(written);
    }

    public Task<UnitBoundary> GetBoundaryAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_boundary.TryGetValue(unitId, out var v) ? v : UnitBoundary.Empty);
    }

    public Task SetBoundaryAsync(Guid unitId, UnitBoundary boundary, CancellationToken cancellationToken = default)
    {
        if (boundary.IsEmpty)
        {
            _boundary.TryRemove(unitId, out _);
        }
        else
        {
            _boundary[unitId] = boundary;
        }
        return Task.CompletedTask;
    }

    public Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            _inheritance.TryGetValue(unitId, out var v) ? v : UnitPermissionInheritance.Inherit);
    }

    public Task SetPermissionInheritanceAsync(
        Guid unitId,
        UnitPermissionInheritance inheritance,
        CancellationToken cancellationToken = default)
    {
        _inheritance[unitId] = inheritance;
        return Task.CompletedTask;
    }

    public Task<ExpertiseDomain[]> GetOwnExpertiseAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_expertise.TryGetValue(unitId, out var v) ? v : []);
    }

    public Task<ExpertiseDomain[]> SetOwnExpertiseAsync(
        Guid unitId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default)
    {
        var normalised = domains
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { Name = g.Key })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
        _expertise[unitId] = normalised;
        _expertiseInitialised[unitId] = true;
        return Task.FromResult(normalised);
    }

    public Task<bool> HasOwnExpertiseSetAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_expertiseInitialised.TryGetValue(unitId, out var v) && v);
    }

    /// <summary>
    /// Test helper: marks <paramref name="unitId"/> as having had its
    /// own-expertise list explicitly initialised, mirroring the
    /// <c>unit_live_config.expertise_initialised</c> flag.
    /// </summary>
    public void SetExpertiseInitialised(Guid unitId, bool value = true)
    {
        _expertiseInitialised[unitId] = value;
    }

    /// <summary>
    /// Test helper: pre-seeds the metadata for <paramref name="unitId"/>
    /// without going through the partial-PATCH semantics, so tests can
    /// arrange a "unit already configured" baseline.
    /// </summary>
    public void SeedMetadata(Guid unitId, UnitMetadata metadata)
    {
        _metadata[unitId] = metadata;
    }

    /// <summary>
    /// Test helper: pre-seeds the boundary for <paramref name="unitId"/>.
    /// </summary>
    public void SeedBoundary(Guid unitId, UnitBoundary boundary)
    {
        if (boundary.IsEmpty)
        {
            _boundary.TryRemove(unitId, out _);
        }
        else
        {
            _boundary[unitId] = boundary;
        }
    }

    /// <summary>
    /// Test helper: pre-seeds the inheritance flag for
    /// <paramref name="unitId"/>.
    /// </summary>
    public void SeedInheritance(Guid unitId, UnitPermissionInheritance inheritance)
    {
        _inheritance[unitId] = inheritance;
    }
}
