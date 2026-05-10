// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF-backed implementation of <see cref="IUnitLiveConfigRepository"/>.
/// Reads and writes <see cref="UnitLiveConfigEntity"/> and
/// <see cref="UnitExpertiseEntity"/> rows; the
/// <see cref="SpringDbContext"/> stamps <c>TenantId</c> from the
/// ambient <c>ITenantContext</c> on insert and applies the per-entity
/// tenant query filter on read.
/// </summary>
public class UnitLiveConfigRepository(SpringDbContext context) : IUnitLiveConfigRepository
{
    /// <inheritdoc />
    public async Task<UnitMetadata> GetMetadataAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        var row = await context.UnitLiveConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UnitId == unitId, cancellationToken);

        if (row is null)
        {
            // No row yet — every field is unset. Returning all-null lets
            // the API layer apply its own defaults without the repository
            // taking a position. DisplayName / Description are always
            // null here; the directory entity owns those.
            return new UnitMetadata(null, null, null, null);
        }

        return new UnitMetadata(
            DisplayName: null,
            Description: null,
            Model: row.Model,
            Color: row.Color,
            Provider: row.Provider,
            Hosting: row.Hosting);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid unitId, UnitMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Identify which actor-owned fields are actually being patched
        // first so we can both decide whether to write and emit an
        // accurate field list on the activity event. DisplayName /
        // Description are ignored — the directory entity owns them.
        var written = new List<string>();
        if (metadata.Model is not null) written.Add(nameof(metadata.Model));
        if (metadata.Color is not null) written.Add(nameof(metadata.Color));
        if (metadata.Provider is not null) written.Add(nameof(metadata.Provider));
        if (metadata.Hosting is not null) written.Add(nameof(metadata.Hosting));

        if (written.Count == 0)
        {
            // No-op patch (every actor-owned field null). Don't
            // materialise a row.
            return Array.Empty<string>();
        }

        var row = await context.UnitLiveConfigs
            .FirstOrDefaultAsync(c => c.UnitId == unitId, cancellationToken);

        if (row is null)
        {
            row = new UnitLiveConfigEntity { UnitId = unitId };
            context.UnitLiveConfigs.Add(row);
        }

        if (metadata.Model is not null) row.Model = metadata.Model;
        if (metadata.Color is not null) row.Color = metadata.Color;
        if (metadata.Provider is not null) row.Provider = metadata.Provider;
        if (metadata.Hosting is not null) row.Hosting = metadata.Hosting;

        await context.SaveChangesAsync(cancellationToken);
        return written;
    }

    /// <inheritdoc />
    public async Task<UnitBoundary> GetBoundaryAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        var element = await context.UnitLiveConfigs
            .AsNoTracking()
            .Where(c => c.UnitId == unitId)
            .Select(c => c.Boundary)
            .FirstOrDefaultAsync(cancellationToken);

        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return UnitBoundary.Empty;
        }

        var deserialised = JsonSerializer.Deserialize<UnitBoundary>(element.Value.GetRawText());
        return deserialised ?? UnitBoundary.Empty;
    }

    /// <inheritdoc />
    public async Task SetBoundaryAsync(Guid unitId, UnitBoundary boundary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        var row = await context.UnitLiveConfigs
            .FirstOrDefaultAsync(c => c.UnitId == unitId, cancellationToken);

        if (row is null)
        {
            row = new UnitLiveConfigEntity { UnitId = unitId };
            context.UnitLiveConfigs.Add(row);
        }

        // Empty boundary is stored as a null jsonb column so the
        // next read returns UnitBoundary.Empty via the column-absent
        // path — semantically identical to an explicit empty boundary.
        row.Boundary = boundary.IsEmpty
            ? null
            : JsonSerializer.SerializeToElement(boundary);

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var row = await context.UnitLiveConfigs
            .AsNoTracking()
            .Where(c => c.UnitId == unitId)
            .Select(c => (UnitPermissionInheritance?)c.PermissionInheritance)
            .FirstOrDefaultAsync(cancellationToken);

        // ADR-0013: absent row means Inherit — ancestor grants cascade
        // by default; only an explicit Isolated row opts out.
        return row ?? UnitPermissionInheritance.Inherit;
    }

    /// <inheritdoc />
    public async Task SetPermissionInheritanceAsync(
        Guid unitId,
        UnitPermissionInheritance inheritance,
        CancellationToken cancellationToken = default)
    {
        var row = await context.UnitLiveConfigs
            .FirstOrDefaultAsync(c => c.UnitId == unitId, cancellationToken);

        if (row is null)
        {
            row = new UnitLiveConfigEntity { UnitId = unitId };
            context.UnitLiveConfigs.Add(row);
        }

        row.PermissionInheritance = inheritance;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetOwnExpertiseAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var rows = await context.UnitExpertise
            .AsNoTracking()
            .Where(e => e.UnitId == unitId)
            .OrderBy(e => e.Name)
            .ToArrayAsync(cancellationToken);

        return [.. rows.Select(ToDomain)];
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> SetOwnExpertiseAsync(
        Guid unitId,
        IReadOnlyList<ExpertiseDomain> domains,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);

        // Normalise: drop blank-name entries, group by name case-
        // insensitively (last write wins so a caller can patch level /
        // description by re-listing the same domain), sort by name.
        var normalised = domains
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { Name = g.Key })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();

        var existing = await context.UnitExpertise
            .Where(e => e.UnitId == unitId)
            .ToListAsync(cancellationToken);

        // Group existing rows by name for fast lookup; collisions are
        // impossible thanks to the unique index but be defensive.
        var existingByName = existing
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var keep = new HashSet<string>(normalised.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        // Remove rows whose names dropped out of the new list.
        foreach (var row in existing)
        {
            if (!keep.Contains(row.Name))
            {
                context.UnitExpertise.Remove(row);
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var domain in normalised)
        {
            if (existingByName.TryGetValue(domain.Name, out var row))
            {
                // Update in place — preserve the original CreatedAt so
                // an audit reader can tell when the unit first declared
                // this domain. The audit trail of mutations lives in
                // the activity-event stream.
                row.Description = domain.Description ?? string.Empty;
                row.Level = domain.Level;
                row.InputSchemaJson = domain.InputSchemaJson;
            }
            else
            {
                context.UnitExpertise.Add(new UnitExpertiseEntity
                {
                    Id = Guid.NewGuid(),
                    UnitId = unitId,
                    Name = domain.Name,
                    Description = domain.Description ?? string.Empty,
                    Level = domain.Level,
                    InputSchemaJson = domain.InputSchemaJson,
                    CreatedAt = now,
                });
            }
        }

        // Mark the live-config row as "expertise initialised" so the
        // activation seeder honours the actor-state-wins precedence
        // rule even when the persisted list is empty.
        var liveConfig = await context.UnitLiveConfigs
            .FirstOrDefaultAsync(c => c.UnitId == unitId, cancellationToken);
        if (liveConfig is null)
        {
            liveConfig = new UnitLiveConfigEntity { UnitId = unitId };
            context.UnitLiveConfigs.Add(liveConfig);
        }
        liveConfig.ExpertiseInitialised = true;

        await context.SaveChangesAsync(cancellationToken);
        return normalised;
    }

    /// <inheritdoc />
    public async Task<bool> HasOwnExpertiseSetAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        return await context.UnitLiveConfigs
            .AsNoTracking()
            .Where(c => c.UnitId == unitId)
            .Select(c => c.ExpertiseInitialised)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static ExpertiseDomain ToDomain(UnitExpertiseEntity e) =>
        new(Name: e.Name, Description: e.Description, Level: e.Level, InputSchemaJson: e.InputSchemaJson);
}
