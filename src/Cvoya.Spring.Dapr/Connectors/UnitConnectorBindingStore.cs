// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Diagnostics;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitConnectorBindingStore"/>.
/// Creates a fresh <c>IServiceScope</c> per call so the scoped
/// <see cref="IUnitConnectorBindingRepository"/> (and its
/// <c>SpringDbContext</c>) resolves cleanly from singleton call sites.
/// Logs the wall-clock duration of every read (<see cref="GetAsync"/>,
/// <see cref="GetMetadataAsync"/>) so the v0.2 cache decision is
/// data-driven (ADR-0040 § 3).
/// </summary>
/// <remarks>
/// <para>
/// #2335 Sub B: connector binds and unbinds also write / revoke
/// <c>unit_tool_grants</c> rows under <c>provenance =
/// "connector:&lt;Slug&gt;"</c>. The wiring is data-driven — every
/// connector that registers an <see cref="IConnectorType"/> and an
/// <see cref="ISkillRegistry"/> participates automatically.
/// <see cref="IConnectorType"/> and <see cref="ISkillRegistry"/> are
/// resolved from the per-call <c>IServiceScope</c> rather than via
/// constructor injection because the IConnectorType graph closes back
/// over <see cref="IUnitConnectorConfigStore"/> →
/// <see cref="IUnitConnectorBindingStore"/> (UnitActorConnectorConfigStore
/// depends on the binding store). Constructor injection would create a
/// singleton cycle that the DI graph cannot resolve.
/// </para>
/// </remarks>
public class UnitConnectorBindingStore(
    IServiceScopeFactory scopeFactory,
    ILogger<UnitConnectorBindingStore> logger) : IUnitConnectorBindingStore
{
    /// <inheritdoc />
    public async Task<UnitConnectorBinding?> GetAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        var result = await repo.GetAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitConnectorBinding.Get unit={UnitId} bound={Bound} elapsedMs={ElapsedMs}",
            unitId, result is not null, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        Guid unitId,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();

        // #2335 Sub B: capture the previous binding so we can revoke its
        // namespace grants atomically with writing the new ones — a re-bind
        // from github → slack must drop github.* rows before slack.* land.
        var previous = await repo.GetAsync(unitId, cancellationToken);

        await repo.SetAsync(unitId, connectorTypeId, config, cancellationToken);

        if (previous is not null && previous.TypeId != connectorTypeId)
        {
            await RevokeNamespaceGrantsAsync(scope.ServiceProvider, unitId, previous.TypeId, cancellationToken);
        }
        await AutoGrantNamespaceAsync(scope.ServiceProvider, unitId, connectorTypeId, cancellationToken);

        logger.LogInformation(
            "Unit {UnitId} bound to connector type {TypeId}",
            unitId, connectorTypeId);
    }

    /// <inheritdoc />
    public async Task ClearAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();

        var previous = await repo.GetAsync(unitId, cancellationToken);
        await repo.ClearAsync(unitId, cancellationToken);

        if (previous is not null)
        {
            await RevokeNamespaceGrantsAsync(scope.ServiceProvider, unitId, previous.TypeId, cancellationToken);
        }

        logger.LogInformation("Unit {UnitId} connector binding cleared", unitId);
    }

    /// <summary>
    /// Writes one <c>unit_tool_grants</c> row per <c>&lt;ToolNamespace&gt;.*</c>
    /// tool exposed by the connector type with
    /// <c>provenance = "connector:&lt;Slug&gt;"</c>. Data-driven: the wiring
    /// has zero per-connector code — a new connector that registers an
    /// <see cref="IConnectorType"/> and an <see cref="ISkillRegistry"/>
    /// gets the same treatment automatically.
    /// </summary>
    private async Task AutoGrantNamespaceAsync(
        IServiceProvider sp,
        Guid unitId,
        Guid connectorTypeId,
        CancellationToken cancellationToken)
    {
        var connectorTypes = sp.GetServices<IConnectorType>();
        var connectorType = ResolveConnectorType(connectorTypes, connectorTypeId);
        if (connectorType is null)
        {
            logger.LogWarning(
                "Unit {UnitId}: connector type {TypeId} is not registered in DI; no auto-grant.",
                unitId, connectorTypeId);
            return;
        }

        var ns = connectorType.ToolNamespace;
        var provenance = ToolProvenance.ConnectorPrefix + connectorType.Slug;
        var skillRegistries = sp.GetServices<ISkillRegistry>();
        var tools = EnumerateToolsInNamespace(skillRegistries, ns).ToList();
        if (tools.Count == 0)
        {
            logger.LogDebug(
                "Connector {Slug} declares namespace '{Namespace}' but no registry exposes tools under it.",
                connectorType.Slug, ns);
            return;
        }

        var db = sp.GetRequiredService<SpringDbContext>();

        // Read existing connector-provenance rows under this provenance so
        // the write is idempotent (the same connector can be re-bound
        // without flapping rows in the audit log).
        var existing = await db.UnitToolGrants
            .Where(g => g.UnitId == unitId && g.Provenance == provenance)
            .ToListAsync(cancellationToken);
        var existingNames = new HashSet<string>(
            existing.Select(g => g.ToolName), StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var tool in tools)
        {
            if (existingNames.Contains(tool.Name))
            {
                continue;
            }
            db.UnitToolGrants.Add(new UnitToolGrantEntity
            {
                UnitId = unitId,
                Namespace = ns,
                ToolName = tool.Name,
                Provenance = provenance,
                CreatedAt = now,
            });
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Auto-granted {Count} tools under namespace '{Namespace}' to unit {UnitId} (provenance {Provenance}).",
                added, ns, unitId, provenance);
        }
    }

    /// <summary>
    /// Removes every <c>unit_tool_grants</c> row written by an earlier
    /// auto-grant for the supplied connector type. Idempotent — runs on
    /// both unbind and re-bind paths.
    /// </summary>
    private async Task RevokeNamespaceGrantsAsync(
        IServiceProvider sp,
        Guid unitId,
        Guid connectorTypeId,
        CancellationToken cancellationToken)
    {
        var connectorTypes = sp.GetServices<IConnectorType>();
        var connectorType = ResolveConnectorType(connectorTypes, connectorTypeId);
        if (connectorType is null)
        {
            // Connector type went away from DI between bind and unbind.
            // Match on provenance shape so we still tidy up — best effort.
            logger.LogWarning(
                "Unit {UnitId}: connector type {TypeId} no longer registered. Skipping namespace revoke.",
                unitId, connectorTypeId);
            return;
        }

        var provenance = ToolProvenance.ConnectorPrefix + connectorType.Slug;
        var db = sp.GetRequiredService<SpringDbContext>();
        var rows = await db.UnitToolGrants
            .Where(g => g.UnitId == unitId && g.Provenance == provenance)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }
        db.UnitToolGrants.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Revoked {Count} connector grants for unit {UnitId} (provenance {Provenance}).",
            rows.Count, unitId, provenance);
    }

    private static IConnectorType? ResolveConnectorType(IEnumerable<IConnectorType> connectorTypes, Guid typeId)
    {
        foreach (var ct in connectorTypes)
        {
            if (ct.TypeId == typeId)
            {
                return ct;
            }
        }
        return null;
    }

    private static IEnumerable<ToolDefinition> EnumerateToolsInNamespace(IEnumerable<ISkillRegistry> skillRegistries, string ns)
    {
        if (string.IsNullOrEmpty(ns))
        {
            yield break;
        }
        foreach (var registry in skillRegistries)
        {
            foreach (var tool in registry.GetToolsByNamespace(ns))
            {
                yield return tool;
            }
        }
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetMetadataAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        var result = await repo.GetMetadataAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitConnectorBinding.GetMetadata unit={UnitId} hasMetadata={HasMetadata} elapsedMs={ElapsedMs}",
            unitId, result is not null, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        Guid unitId,
        JsonElement metadata,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        await repo.SetMetadataAsync(unitId, metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearMetadataAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        await repo.ClearMetadataAsync(unitId, cancellationToken);
    }
}
