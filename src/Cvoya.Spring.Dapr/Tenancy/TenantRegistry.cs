// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using System.Text.RegularExpressions;

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
/// structured audit signal. The OSS bypass implementation only logs;
/// the cloud overlay's permission-checked variant gates the open on
/// the caller's role, so wrapping every call here lets the cloud
/// override apply uniformly without each call site having to know.
/// </para>
/// <para>
/// Id-shape validation lives here rather than at the API layer so the
/// registry stays self-defending — a callsite that bypasses the
/// endpoints (a CLI script, a private-cloud admin tool) cannot create
/// a tenant with a malformed id.
/// </para>
/// </remarks>
public sealed partial class TenantRegistry(
    SpringDbContext dbContext,
    ITenantScopeBypass tenantScopeBypass,
    ILogger<TenantRegistry> logger) : ITenantRegistry
{
    /// <summary>
    /// Stable lower-case slug shape for tenant ids:
    /// 1–64 chars; first char alphanumeric; remaining chars alphanumeric,
    /// underscore, or hyphen. Mirrors the shape we recommend for
    /// <see cref="ITenantScopedEntity.TenantId"/> values throughout the
    /// platform.
    /// </summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex TenantIdShape();

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
    public async Task<TenantRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var bypass = tenantScopeBypass.BeginBypass("platform-tenants get");
        var row = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Project(row);
    }

    /// <inheritdoc />
    public async Task<TenantRecord> CreateAsync(
        string id,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!TenantIdShape().IsMatch(id))
        {
            throw new ArgumentException(
                $"Tenant id '{id}' does not match the required shape (lower-case alphanumerics + '_' / '-', 1-64 chars).",
                nameof(id));
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
                    ? $"Tenant '{id}' already exists."
                    : $"Tenant '{id}' was previously soft-deleted; restore is out of scope for v0.1.");
        }

        var now = DateTimeOffset.UtcNow;
        var resolvedDisplay = string.IsNullOrWhiteSpace(displayName) ? id : displayName!;
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
        string id,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

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
            row.DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        }

        row.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Updated tenant '{TenantId}' (display '{DisplayName}').", row.Id, row.DisplayName);
        return Project(row);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

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