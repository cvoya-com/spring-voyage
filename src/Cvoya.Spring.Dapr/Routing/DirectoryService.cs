// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implementation of <see cref="IDirectoryService"/> backed by a <see cref="DirectoryCache"/>
/// with write-through persistence to Postgres via <see cref="SpringDbContext"/>.
/// The in-memory cache provides the fast-path for reads; all mutations are persisted
/// to the database so directory entries survive container restarts.
/// </summary>
public class DirectoryService(
    DirectoryCache cache,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IDirectoryService
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
            entry.Address.Scheme, entry.Address.Path, entry.ActorId);
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

        var existing = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.UnitId == entry.Address.Path, cancellationToken);

        if (existing is not null)
        {
            existing.ActorId = entry.ActorId;
            existing.Name = entry.DisplayName;
            existing.Description = entry.Description;
            existing.DeletedAt = null;
        }
        else
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Guid.NewGuid(),
                UnitId = entry.Address.Path,
                ActorId = entry.ActorId,
                Name = entry.DisplayName,
                Description = entry.Description,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertAgentAsync(DirectoryEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.AgentId == entry.Address.Path, cancellationToken);

        if (existing is not null)
        {
            existing.ActorId = entry.ActorId;
            existing.Name = entry.DisplayName;
            existing.Role = entry.Role;
            existing.DeletedAt = null;
        }
        else
        {
            db.AgentDefinitions.Add(new AgentDefinitionEntity
            {
                Id = Guid.NewGuid(),
                AgentId = entry.Address.Path,
                ActorId = entry.ActorId,
                Name = entry.DisplayName,
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
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var entity = await db.UnitDefinitions
                .FirstOrDefaultAsync(u => u.UnitId == address.Path, cancellationToken);

            if (entity is not null)
            {
                entity.DeletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        else if (string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var entity = await db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.AgentId == address.Path, cancellationToken);

            if (entity is not null)
            {
                entity.DeletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<DirectoryEntry?> LoadFromDatabaseAsync(Address address, CancellationToken cancellationToken)
    {
        var scheme = address.Scheme;

        if (string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var entity = await db.UnitDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.UnitId == address.Path, cancellationToken);

            if (entity is not null)
            {
                return new DirectoryEntry(
                    address,
                    entity.ActorId ?? entity.UnitId,
                    entity.Name,
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
                .FirstOrDefaultAsync(a => a.AgentId == address.Path, cancellationToken);

            if (entity is not null)
            {
                return new DirectoryEntry(
                    address,
                    entity.ActorId ?? entity.AgentId,
                    entity.Name,
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
            var address = new Address("unit", u.UnitId);
            var entry = new DirectoryEntry(
                address,
                u.ActorId ?? u.UnitId,
                u.Name,
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
            var address = new Address("agent", a.AgentId);
            var entry = new DirectoryEntry(
                address,
                a.ActorId ?? a.AgentId,
                a.Name,
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