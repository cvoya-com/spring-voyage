// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitOrchestrationStore"/> (#606). Reads and writes
/// the <c>orchestration.strategy</c> key on the persisted
/// <c>UnitDefinitions.Definition</c> JSON — the same document
/// <see cref="DbOrchestrationStrategyProvider"/> reads at dispatch time.
/// </summary>
/// <remarks>
/// <para>
/// Lookup is by <c>UnitDefinitionEntity.UnitId</c> because the HTTP and CLI
/// surfaces address units by their user-facing name. The <c>orchestration</c>
/// slot is rewritten in place and every other property on the Definition
/// document (instructions / expertise / execution) is preserved verbatim.
/// Passing a <c>null</c> or whitespace key strips the slot entirely so the
/// resolver falls through to policy inference / the unkeyed default.
/// </para>
/// <para>
/// Successful writes fire <see cref="IOrchestrationStrategyCacheInvalidator.Invalidate(string)"/>
/// so the in-process provider cache (#518) sees the change on the next
/// message dispatched to the unit rather than waiting for its TTL. The
/// cache keys on the Dapr actor id, so the store looks the actor id up
/// from the same row it's about to update and passes it through.
/// </para>
/// </remarks>
public class DbUnitOrchestrationStore(
    IServiceScopeFactory scopeFactory,
    IOrchestrationStrategyCacheInvalidator cacheInvalidator,
    ILoggerFactory loggerFactory) : IUnitOrchestrationStore
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbUnitOrchestrationStore>();

    /// <inheritdoc />
    public async Task<string?> GetStrategyKeyAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId)
            || !Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var unitUuid))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Id == unitUuid && u.DeletedAt == null,
                cancellationToken);

        return entity is null ? null : ExtractStrategyKey(entity.Definition);
    }

    /// <inheritdoc />
    public async Task SetStrategyKeyAsync(
        string unitId,
        string? strategyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            throw new ArgumentException("Unit id must be supplied.", nameof(unitId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var unitUuid))
        {
            throw new ArgumentException(
                $"Unit id '{unitId}' is not a valid Guid.", nameof(unitId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(
                u => u.Id == unitUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning(
                "Unit '{UnitId}': no UnitDefinition row found; orchestration strategy not persisted.",
                unitId);
            return;
        }

        var payload = new Dictionary<string, object?>();

        // Preserve every other property already on the Definition document.
        // Preserve unrelated manifest-applied slots that share the same
        // Definition document (expertise, instructions, execution, ...).
        if (entity.Definition is { ValueKind: JsonValueKind.Object } existing)
        {
            foreach (var prop in existing.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "orchestration", StringComparison.OrdinalIgnoreCase))
                {
                    payload[prop.Name] = prop.Value;
                }
            }
        }

        var trimmed = strategyKey?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            // Persist alongside every preserved property. The orchestration
            // slot is a single-key object today so follow-up work can layer
            // per-strategy options here without reshaping the write path
            // (see ADR-0010 revisit criteria).
            payload["orchestration"] = new { strategy = trimmed };
        }
        // else: null / blank strategy → slot is stripped (fall-through to
        // policy inference and the unkeyed default per ADR-0010).

        entity.Definition = JsonSerializer.SerializeToElement(payload);
        await db.SaveChangesAsync(cancellationToken);

        // The caching provider keys on the Dapr actor id because that is
        // what UnitActor.HandleDomainMessageAsync passes at dispatch time.
        // The unit's actor id is the canonical no-dash 32-char form of its
        // Guid identity (post-#1629).
        cacheInvalidator.Invalidate(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entity.Id));
    }

    /// <summary>
    /// Extracts the <c>orchestration.strategy</c> string from a persisted
    /// definition JSON document. Matches the tolerance contract on
    /// <see cref="DbOrchestrationStrategyProvider.ExtractStrategyKey"/> —
    /// missing block, wrong JSON shape, empty string all surface as
    /// <c>null</c> so the resolver can continue through its fallback
    /// ladder.
    /// </summary>
    internal static string? ExtractStrategyKey(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        if (!element.TryGetProperty("orchestration", out var orchestration)
            || orchestration.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!orchestration.TryGetProperty("strategy", out var strategy)
            || strategy.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var key = strategy.GetString();
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}