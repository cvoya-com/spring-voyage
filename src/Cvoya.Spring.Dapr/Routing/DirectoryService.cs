// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implementation of <see cref="IDirectoryService"/> backed by a <see cref="DirectoryCache"/>
/// with write-through persistence to Postgres via <see cref="SpringDbContext"/>.
/// The in-memory cache provides the fast-path for reads; all mutations are persisted
/// to the database so directory entries survive container restarts.
/// </summary>
/// <remarks>
/// <paramref name="actorProxyFactory"/> is optional to keep legacy test harnesses
/// (which construct <see cref="DirectoryService"/> with only DB dependencies) working.
/// When absent, <see cref="UnregisterAsync"/> falls back to the pre-#652 behaviour of
/// only soft-deleting the unit row — sub-unit recursion (which requires reading
/// <see cref="IUnitActor.GetMembersAsync"/> from actor state, the only place sub-unit
/// membership is materialised) is skipped. Production DI wires the factory so the full
/// cascade runs.
/// </remarks>
public class DirectoryService(
    DirectoryCache cache,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IActorProxyFactory? actorProxyFactory = null) : IDirectoryService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DirectoryService>();
    private readonly ConcurrentDictionary<string, DirectoryEntry> _entries = new();

    /// <inheritdoc />
    public async Task RegisterAsync(DirectoryEntry entry, CancellationToken cancellationToken = default)
    {
        var key = ToKey(entry.Address);
        _entries[key] = entry;
        cache.Set(entry.Address, entry);

        await PersistEntryAsync(entry, cancellationToken);

        _logger.LogInformation("Registered directory entry for {Scheme}://{Path} with actor ID {ActorId}",
            entry.Address.Scheme, entry.Address.Path, GuidFormatter.Format(entry.ActorId));
    }

    /// <inheritdoc />
    public async Task UnregisterAsync(Address address, CancellationToken cancellationToken = default)
    {
        var key = ToKey(address);
        _entries.TryRemove(key, out _);
        cache.Invalidate(address);

        await DeleteEntryAsync(address, cancellationToken);

        _logger.LogInformation("Unregistered directory entry for {Scheme}://{Path}",
            address.Scheme, address.Path);
    }

    /// <inheritdoc />
    public async Task<DirectoryEntry?> ResolveAsync(Address address, CancellationToken cancellationToken = default)
    {
        if (cache.TryGet(address, out var cached))
        {
            return cached;
        }

        var key = ToKey(address);
        if (_entries.TryGetValue(key, out var entry))
        {
            cache.Set(address, entry);
            return entry;
        }

        // Cache miss — fall back to the database.
        var dbEntry = await LoadFromDatabaseAsync(address, cancellationToken);
        if (dbEntry is not null)
        {
            _entries[key] = dbEntry;
            cache.Set(address, dbEntry);
        }

        return dbEntry;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryEntry>> ResolveByRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        await EnsureCacheWarmedAsync(cancellationToken);

        var matches = _entries.Values
            .Where(e => string.Equals(e.Role, role, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryEntry>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCacheWarmedAsync(cancellationToken);
        return _entries.Values.ToList();
    }

    /// <inheritdoc />
    public async Task<DirectoryEntry?> UpdateEntryAsync(
        Address address,
        string? displayName,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var key = ToKey(address);

        if (!_entries.TryGetValue(key, out var existing))
        {
            // Try loading from the database in case the cache is cold.
            existing = await LoadFromDatabaseAsync(address, cancellationToken);
            if (existing is null)
            {
                return null;
            }
        }

        // Null fields mean "leave unchanged" so partial PATCH-style updates are supported.
        var updated = existing with
        {
            DisplayName = displayName ?? existing.DisplayName,
            Description = description ?? existing.Description,
        };

        _entries[key] = updated;
        cache.Set(address, updated);

        await PersistEntryAsync(updated, cancellationToken);

        _logger.LogInformation(
            "Updated directory entry for {Scheme}://{Path} (displayName changed: {DisplayNameChanged}, description changed: {DescriptionChanged})",
            address.Scheme,
            address.Path,
            displayName is not null,
            description is not null);

        return updated;
    }

    private async Task PersistEntryAsync(DirectoryEntry entry, CancellationToken cancellationToken)
    {
        var scheme = entry.Address.Scheme;

        if (string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            await UpsertUnitAsync(entry, cancellationToken);
        }
        else if (string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase))
        {
            await UpsertAgentAsync(entry, cancellationToken);
        }
        else
        {
            _logger.LogDebug("Directory entry for scheme {Scheme} is cache-only; no DB persistence.",
                scheme);
        }
    }

    private async Task UpsertUnitAsync(DirectoryEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Identity is the entry's Guid id (== Address.Id == ActorId). Look up
        // the row by primary key; if it exists (active or soft-deleted) we
        // refresh and clear DeletedAt, otherwise we insert a fresh row.
        var id = entry.ActorId;

        var existing = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (existing is not null)
        {
            existing.DisplayName = entry.DisplayName;
            existing.Description = entry.Description;
            existing.DeletedAt = null;
        }
        else
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = id,
                DisplayName = entry.DisplayName,
                Description = entry.Description,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertAgentAsync(DirectoryEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var id = entry.ActorId;

        var existing = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (existing is not null)
        {
            existing.DisplayName = entry.DisplayName;
            existing.Role = entry.Role;
            existing.DeletedAt = null;
        }
        else
        {
            db.AgentDefinitions.Add(new AgentDefinitionEntity
            {
                Id = id,
                DisplayName = entry.DisplayName,
                Role = entry.Role,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task DeleteEntryAsync(Address address, CancellationToken cancellationToken)
    {
        var scheme = address.Scheme;

        if (string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            // Cascade: sub-units first, then memberships + ref-counted agents,
            // then the unit row itself (#652). Sub-unit membership only lives in
            // the unit actor's state, so we ask the proxy for the current member
            // list before touching the DB. Actor unavailability degrades to
            // "no sub-units found" — the unit still soft-deletes and its
            // memberships still get cleaned up.
            var visited = new HashSet<Guid>();
            await CascadeDeleteUnitAsync(address, visited, cancellationToken);
        }
        else if (string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var entity = await db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.Id == address.Id, cancellationToken);

            if (entity is not null)
            {
                entity.DeletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Recursively soft-deletes a unit and cascades to its sub-units and
    /// memberships (#652). One DB unit-of-work per unit visited — the subtree
    /// is walked depth-first via the unit actor's member list so a single
    /// parent delete cleans every descendant row in one call chain.
    /// </summary>
    private async Task CascadeDeleteUnitAsync(
        Address unitAddress,
        HashSet<Guid> visited,
        CancellationToken cancellationToken)
    {
        var unitId = unitAddress.Id;
        if (!visited.Add(unitId))
        {
            // Already processed in this cascade — skip to avoid pathological
            // re-entry if actor state ever contains a cycle we didn't prevent.
            return;
        }

        // Discover sub-units from actor state before we soft-delete the row.
        var subUnits = await TryReadUnitMembersAsync(unitAddress, cancellationToken);

        foreach (var sub in subUnits)
        {
            if (string.Equals(sub.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                await CascadeDeleteUnitAsync(sub, visited, cancellationToken);
                var subKey = ToKey(sub);
                _entries.TryRemove(subKey, out _);
                cache.Invalidate(sub);
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(u => u.Id == unitId, cancellationToken);

        // Already-deleted or missing unit: short-circuit. Matches the pre-#652
        // "not found" semantic where DeleteEntryAsync silently returns.
        if (entity is null)
        {
            return;
        }

        // Load every membership edge into the tracked change set so the same
        // SaveChangesAsync that flips the unit's DeletedAt also hard-deletes
        // the rows and commits the agent ref-count decisions below.
        var memberships = await db.UnitMemberships
            .Where(m => m.UnitId == unitId)
            .ToListAsync(cancellationToken);

        foreach (var membership in memberships)
        {
            db.UnitMemberships.Remove(membership);
        }

        // #1154: tear down every persistent sub-unit edge that mentions
        // this unit on either side.
        var subunitEdges = await db.UnitSubunitMemberships
            .Where(e => e.ParentId == unitId || e.ChildId == unitId)
            .ToListAsync(cancellationToken);

        foreach (var edge in subunitEdges)
        {
            db.UnitSubunitMemberships.Remove(edge);
        }

        // #1488: delete the unit's policy row.
        var policyRow = await db.UnitPolicies
            .FirstOrDefaultAsync(p => p.UnitId == unitId, cancellationToken);
        if (policyRow is not null)
        {
            db.UnitPolicies.Remove(policyRow);
        }

        // #1488: delete all unit-scoped secret registry entries for this
        // specific unit instance. Secret rows are keyed by OwnerId = unit id.
        var secretRows = await db.SecretRegistryEntries
            .Where(e => e.Scope == SecretScope.Unit && e.OwnerId == unitId)
            .ToListAsync(cancellationToken);
        if (secretRows.Count > 0)
        {
            db.SecretRegistryEntries.RemoveRange(secretRows);
        }

        // Ref-count each affected agent. An agent is soft-deleted iff every
        // other unit it's attached to is already deleted.
        foreach (var membership in memberships)
        {
            var agentUuid = membership.AgentId;

            var agentEntity = await db.AgentDefinitions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.Id == agentUuid, cancellationToken);

            // Check for surviving memberships via UUID keys (excluding this unit).
            var otherUnitIds = await db.UnitMemberships
                .Where(m => m.AgentId == agentUuid && m.UnitId != unitId)
                .Select(m => m.UnitId)
                .ToListAsync(cancellationToken);

            var hasLiveOtherUnit = false;
            if (otherUnitIds.Count > 0)
            {
                hasLiveOtherUnit = await db.UnitDefinitions
                    .Where(u => otherUnitIds.Contains(u.Id) && u.DeletedAt == null)
                    .AnyAsync(cancellationToken);
            }

            if (hasLiveOtherUnit)
            {
                // Shared agent: only the edge was removed. Its directory
                // entry stays live.
                continue;
            }

            if (agentEntity is null)
            {
                continue;
            }

            // Agent has no surviving unit membership — soft-delete it.
            if (agentEntity.DeletedAt is null)
            {
                agentEntity.DeletedAt = DateTimeOffset.UtcNow;
            }

            // Evict from the in-memory map + cache so the next resolve falls
            // through to the DB and sees "deleted".
            var agentAddress = new Address("agent", agentEntity.Id);
            var agentKey = ToKey(agentAddress);
            _entries.TryRemove(agentKey, out _);
            cache.Invalidate(agentAddress);
        }

        entity.DeletedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        var unitKey = ToKey(unitAddress);
        _entries.TryRemove(unitKey, out _);
        cache.Invalidate(unitAddress);

        _logger.LogInformation(
            "Cascade-deleted unit {UnitId}: memberships removed={MembershipCount}, sub-units visited={SubUnitCount}, sub-unit edges removed={SubunitEdgeCount}.",
            unitId, memberships.Count, subUnits.Count, subunitEdges.Count);
    }

    /// <summary>
    /// Best-effort read of a unit's current member addresses via the unit
    /// actor proxy.
    /// </summary>
    private async Task<IReadOnlyList<Address>> TryReadUnitMembersAsync(
        Address unitAddress, CancellationToken cancellationToken)
    {
        if (actorProxyFactory is null)
        {
            return Array.Empty<Address>();
        }

        var key = ToKey(unitAddress);
        DirectoryEntry? entry;
        if (!_entries.TryGetValue(key, out entry))
        {
            entry = await LoadFromDatabaseAsync(unitAddress, cancellationToken);
        }
        if (entry is null)
        {
            return Array.Empty<Address>();
        }

        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(GuidFormatter.Format(entry.ActorId)), nameof(UnitActor));
            var members = await proxy.GetMembersAsync(cancellationToken);
            return members ?? Array.Empty<Address>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cascade delete: failed to read members of {Unit}; sub-unit recursion will be skipped.",
                unitAddress);
            return Array.Empty<Address>();
        }
    }

    private async Task<DirectoryEntry?> LoadFromDatabaseAsync(Address address, CancellationToken cancellationToken)
    {
        var scheme = address.Scheme;
        var id = address.Id;

        if (string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var entity = await db.UnitDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

            if (entity is not null)
            {
                return new DirectoryEntry(
                    address,
                    entity.Id,
                    entity.DisplayName,
                    entity.Description ?? string.Empty,
                    Role: null,
                    entity.CreatedAt);
            }
        }
        else if (string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var entity = await db.AgentDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

            if (entity is not null)
            {
                return new DirectoryEntry(
                    address,
                    entity.Id,
                    entity.DisplayName,
                    entity.Description ?? string.Empty,
                    entity.Role,
                    entity.CreatedAt);
            }
        }

        return null;
    }

    /// <summary>
    /// Tracks whether the in-memory dictionary has been warmed from the database.
    /// </summary>
    private volatile bool _warmed;

    /// <summary>
    /// Ensures the in-memory dictionary is populated from the database on the
    /// first call to <see cref="ListAllAsync"/> or <see cref="ResolveByRoleAsync"/>.
    /// Subsequent calls are no-ops.
    /// </summary>
    private async Task EnsureCacheWarmedAsync(CancellationToken cancellationToken)
    {
        if (_warmed)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var units = await db.UnitDefinitions
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var u in units)
        {
            var address = new Address("unit", u.Id);
            var entry = new DirectoryEntry(
                address,
                u.Id,
                u.DisplayName,
                u.Description ?? string.Empty,
                Role: null,
                u.CreatedAt);

            var key = ToKey(address);
            _entries.TryAdd(key, entry);
            cache.Set(address, entry);
        }

        var agents = await db.AgentDefinitions
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var a in agents)
        {
            var address = new Address("agent", a.Id);
            var entry = new DirectoryEntry(
                address,
                a.Id,
                a.DisplayName,
                a.Description ?? string.Empty,
                a.Role,
                a.CreatedAt);

            var key = ToKey(address);
            _entries.TryAdd(key, entry);
            cache.Set(address, entry);
        }

        _warmed = true;

        _logger.LogInformation(
            "Directory cache warmed from database: {UnitCount} unit(s), {AgentCount} agent(s).",
            units.Count, agents.Count);
    }

    private static string ToKey(Address address) => $"{address.Scheme}://{address.Path}";
}