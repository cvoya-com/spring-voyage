// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of
/// <see cref="IUnitStateCoordinator"/>. Owns the persisted-config CRUD
/// concern extracted from <c>UnitActor</c>: reading and writing the
/// unit's metadata, boundary, permission-inheritance flag, and
/// own-expertise list, and emitting the corresponding
/// <see cref="ActivityEventType.StateChanged"/> activity events.
/// </summary>
/// <remarks>
/// Per ADR-0040 / #2049 every read and write goes through
/// <see cref="IUnitLiveConfigStore"/>, which in turn drives the
/// <c>unit_live_config</c> and <c>unit_expertise</c> EF tables. There
/// is no actor-state copy.
/// </remarks>
public class UnitStateCoordinator(
    IUnitLiveConfigStore liveConfigStore,
    ILogger<UnitStateCoordinator> logger) : IUnitStateCoordinator
{
    /// <inheritdoc />
    public async Task<UnitMetadata> GetMetadataAsync(
        string unitId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            // Legacy / non-UUID actor id — there is no EF row to read.
            // Returning all-null lets API surfaces apply their own
            // defaults the same way they would for a row-less unit.
            return new UnitMetadata(null, null, null, null);
        }

        return await liveConfigStore.GetMetadataAsync(unitGuid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        string unitId,
        UnitMetadata metadata,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(emitActivity);

        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            logger.LogWarning(
                "Unit {UnitId} SetMetadata received non-UUID actor id; cannot persist EF state.",
                unitId);
            return;
        }

        var written = await liveConfigStore.UpsertMetadataAsync(unitGuid, metadata, cancellationToken);

        // Even when the actor-owned fields are all null, the
        // directory-owned fields (DisplayName / Description) might be
        // present. The actor decides whether to emit a directory-only
        // event; this coordinator only emits when it actually persisted
        // an actor-owned field, matching the AgentStateCoordinator
        // pattern.
        if (written.Count == 0)
        {
            logger.LogDebug(
                "Unit {UnitId} SetMetadataAsync called with no actor-owned fields to write; nothing to emit.",
                unitId);
            return;
        }

        logger.LogInformation(
            "Unit {UnitId} metadata updated: {Fields}",
            unitId, string.Join(",", written));

        await emitActivity(
            BuildEvent(
                unitId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Unit metadata updated: {string.Join(", ", written)}",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "UnitMetadataUpdated",
                    fields = written,
                    model = metadata.Model,
                    color = metadata.Color,
                    provider = metadata.Provider,
                    hosting = metadata.Hosting,
                    specialty = metadata.Specialty,
                    enabled = metadata.Enabled,
                    executionMode = metadata.ExecutionMode?.ToString(),
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UnitBoundary> GetBoundaryAsync(
        string unitId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            return UnitBoundary.Empty;
        }

        return await liveConfigStore.GetBoundaryAsync(unitGuid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetBoundaryAsync(
        string unitId,
        UnitBoundary boundary,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(emitActivity);

        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            logger.LogWarning(
                "Unit {UnitId} SetBoundary received non-UUID actor id; cannot persist EF state.",
                unitId);
            return;
        }

        await liveConfigStore.SetBoundaryAsync(unitGuid, boundary, cancellationToken);

        await emitActivity(
            BuildEvent(
                unitId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Unit boundary updated. Opacities={boundary.Opacities?.Count ?? 0}, Projections={boundary.Projections?.Count ?? 0}, Syntheses={boundary.Syntheses?.Count ?? 0}",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "UnitBoundaryUpdated",
                    opacities = boundary.Opacities?.Count ?? 0,
                    projections = boundary.Projections?.Count ?? 0,
                    syntheses = boundary.Syntheses?.Count ?? 0,
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetPermissionInheritanceAsync(
        string unitId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            return (int)UnitPermissionInheritance.Inherit;
        }

        var value = await liveConfigStore.GetPermissionInheritanceAsync(unitGuid, cancellationToken);
        return (int)value;
    }

    /// <inheritdoc />
    public async Task SetPermissionInheritanceAsync(
        string unitId,
        int inheritance,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emitActivity);

        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            logger.LogWarning(
                "Unit {UnitId} SetPermissionInheritance received non-UUID actor id; cannot persist EF state.",
                unitId);
            return;
        }

        var typed = (UnitPermissionInheritance)inheritance;
        await liveConfigStore.SetPermissionInheritanceAsync(unitGuid, typed, cancellationToken);

        await emitActivity(
            BuildEvent(
                unitId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Unit permission inheritance updated to {typed}",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "UnitPermissionInheritanceUpdated",
                    inheritance = typed.ToString(),
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetOwnExpertiseAsync(
        string unitId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            return [];
        }

        return await liveConfigStore.GetOwnExpertiseAsync(unitGuid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetOwnExpertiseAsync(
        string unitId,
        ExpertiseDomain[] domains,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(emitActivity);

        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            logger.LogWarning(
                "Unit {UnitId} SetOwnExpertise received non-UUID actor id; cannot persist EF state.",
                unitId);
            return;
        }

        var persisted = await liveConfigStore.SetOwnExpertiseAsync(unitGuid, domains, cancellationToken);

        await emitActivity(
            BuildEvent(
                unitId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Unit expertise updated. Domains: {persisted.Length}",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "UnitExpertiseUpdated",
                    count = persisted.Length,
                    domains = persisted.Select(d => new { d.Name, d.Description, Level = d.Level?.ToString() }),
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HasOwnExpertiseSetAsync(
        string unitId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            return false;
        }

        return await liveConfigStore.HasOwnExpertiseSetAsync(unitGuid, cancellationToken);
    }

    private static ActivityEvent BuildEvent(
        string unitId,
        ActivityEventType eventType,
        ActivitySeverity severity,
        string summary,
        JsonElement? details = null,
        string? correlationId = null)
    {
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Core.Messaging.Address.For("unit", unitId),
            eventType,
            severity,
            summary,
            details,
            correlationId);
    }
}
