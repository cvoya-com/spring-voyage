// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default EF Core-backed implementation of <see cref="ITenantRegistry"/>.
/// Persists rows to <c>spring.tenants</c>.
/// </summary>
/// <remarks>
/// <para>
/// The registry's reads and writes are inherently cross-tenant — every
/// row in <c>spring.tenants</c> represents a different tenant. The
/// underlying entity is global (no tenant query filter applied), but
/// every method still wraps its work in
/// <see cref="ITenantScopeBypass.BeginBypass(string)"/> for the
/// structured audit signal.
/// </para>
/// </remarks>
public sealed class TenantRegistry(
    SpringDbContext dbContext,
    ITenantScopeBypass tenantScopeBypass,
    ILogger<TenantRegistry> logger) : ITenantRegistry
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var bypass = tenantScopeBypass.BeginBypass("platform-tenants list");
        var rows = await dbContext.Tenants
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(Project).ToList();
    }

    /// <inheritdoc />
    public async Task<TenantRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be Guid.Empty.", nameof(id));
        }

        using var bypass = tenantScopeBypass.BeginBypass("platform-tenants get");
        var row = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Project(row);
    }

    /// <inheritdoc />
    public async Task<TenantRecord> CreateAsync(
        Guid id,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be Guid.Empty.", nameof(id));
        }

        using var bypass = tenantScopeBypass.BeginBypass("platform-tenants create");

        // IgnoreQueryFilters so soft-deleted rows surface — re-creating
        // a previously-deleted tenant must collide rather than silently
        // duplicate. Operators wanting to recycle a tenant id explicitly
        // null DeletedAt via a separate restore flow (out of scope for
        // v0.1).
        var existing = await dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                existing.DeletedAt is null
                    ? $"Tenant '{GuidFormatter.Format(id)}' already exists."
                    : $"Tenant '{GuidFormatter.Format(id)}' was previously soft-deleted; restore is out of scope for v0.1.");
        }

        var now = DateTimeOffset.UtcNow;
        var resolvedDisplay = string.IsNullOrWhiteSpace(displayName) ? GuidFormatter.Format(id) : displayName!;
        var entity = new TenantRecordEntity
        {
            Id = id,
            DisplayName = resolvedDisplay,
            State = TenantState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Tenants.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Created tenant '{TenantId}' ({DisplayName}).", id, resolvedDisplay);
        return Project(entity);
    }

    /// <inheritdoc />
    public async Task<TenantRecord?> UpdateAsync(
        Guid id,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be Guid.Empty.", nameof(id));
        }

        using var bypass = tenantScopeBypass.BeginBypass("platform-tenants update");

        var row = await dbContext.Tenants
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (displayName is not null)
        {
            row.DisplayName = string.IsNullOrWhiteSpace(displayName) ? GuidFormatter.Format(id) : displayName;
        }

        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Updated tenant '{TenantId}' (display '{DisplayName}').", row.Id, row.DisplayName);
        return Project(row);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be Guid.Empty.", nameof(id));
        }

        using var bypass = tenantScopeBypass.BeginBypass("platform-tenants delete");

        var row = await dbContext.Tenants
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        row.DeletedAt = now;
        row.State = TenantState.Deleted;
        row.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Soft-deleted tenant '{TenantId}'.", id);
        return true;
    }

    private static TenantRecord Project(TenantRecordEntity entity) =>
        new(
            entity.Id,
            entity.DisplayName,
            entity.State,
            entity.CreatedAt,
            entity.UpdatedAt);
}
