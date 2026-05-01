// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default scoped implementation of <see cref="IHumanIdentityResolver"/>.
/// Resolves username ↔ UUID by querying the <c>humans</c> table. On a
/// cache miss for a username, performs an upsert (INSERT … ON CONFLICT DO
/// NOTHING) so concurrent first-login requests for the same user converge
/// on a single row and UUID. Results are cached in a per-request dictionary.
/// </summary>
internal sealed class HumanIdentityResolver(
    SpringDbContext db,
    ILogger<HumanIdentityResolver> logger)
    : IHumanIdentityResolver
{
    // Per-request caches in both directions.
    private readonly Dictionary<string, Guid> _usernameToId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, HumanEntity> _idToEntity = [];

    /// <inheritdoc />
    public async Task<Guid> ResolveByUsernameAsync(
        string username,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username must not be null or whitespace.", nameof(username));
        }

        if (_usernameToId.TryGetValue(username, out var cached))
        {
            return cached;
        }

        // Look up existing row.
        var existing = await db.Humans
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Username == username, cancellationToken);

        if (existing is not null)
        {
            Cache(existing);
            return existing.Id;
        }

        // No row yet — create one. Concurrent upserts are resolved by the
        // unique index on (tenant_id, username); the losing writer simply
        // re-reads the row.
        var newId = Guid.NewGuid();
        var entity = new HumanEntity
        {
            Id = newId,
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
        };

        try
        {
            db.Humans.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Created Human row for username {Username} with id {HumanId}",
                username, newId);
            Cache(entity);
            return newId;
        }
        catch (DbUpdateException)
        {
            // Race: another request already inserted the row. Detach the
            // conflicting entry so the context is clean, then re-read.
            db.Entry(entity).State = EntityState.Detached;

            var winner = await db.Humans
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.Username == username, cancellationToken);

            if (winner is not null)
            {
                Cache(winner);
                return winner.Id;
            }

            // Should never happen — the unique constraint guarantees a row exists.
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetUsernameAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (_idToEntity.TryGetValue(id, out var cached))
        {
            return cached.Username;
        }

        var entity = await db.Humans
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

        if (entity is not null)
        {
            Cache(entity);
            return entity.Username;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<string?> GetDisplayNameAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (_idToEntity.TryGetValue(id, out var cached))
        {
            return cached.DisplayName;
        }

        var entity = await db.Humans
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

        if (entity is not null)
        {
            Cache(entity);
            return entity.DisplayName;
        }

        return null;
    }

    private void Cache(HumanEntity entity)
    {
        _usernameToId[entity.Username] = entity.Id;
        _idToEntity[entity.Id] = entity;
    }
}