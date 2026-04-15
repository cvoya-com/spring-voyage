// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core-backed <see cref="ISecretRegistry"/> that stores the
/// (tenant, scope, owner, name, version) → storeKey mapping in the
/// <c>secret_registry_entries</c> table. Every query is filtered by the
/// current tenant resolved from <see cref="ITenantContext"/> so a
/// caller in tenant A can never observe or modify entries owned by
/// tenant B.
///
/// <para>
/// <b>Multi-version coexistence (wave 7 A5).</b> Each version is a
/// separate row — the primary-key-ish structural identifier is
/// <c>(TenantId, Scope, OwnerId, Name, Version)</c>, enforced by a
/// unique index. Lookups default to the MAX(Version) row; pinned
/// lookups select a specific version. Rotation inserts a new row at
/// <c>max(Version)+1</c>. Prune removes older rows while retaining the
/// current one.
/// </para>
/// </summary>
public class EfSecretRegistry : ISecretRegistry
{
    private readonly SpringDbContext _db;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new <see cref="EfSecretRegistry"/>.
    /// </summary>
    public EfSecretRegistry(SpringDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task RegisterAsync(SecretRef @ref, string storeKey, SecretOrigin origin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        var tenant = _tenantContext.CurrentTenantId;

        // Register is a wipe-and-replace primitive: any existing chain
        // (all versions) is removed and replaced by a single fresh row
        // at version 1. This preserves the pre-A5 POST semantics —
        // POST creates a brand-new chain, PUT (rotate) appends. The
        // rationale is documented in ISecretRegistry.RegisterAsync XML.
        var chain = await _db.SecretRegistryEntries
            .Where(e => e.TenantId == tenant
                     && e.Scope == @ref.Scope
                     && e.OwnerId == @ref.OwnerId
                     && e.Name == @ref.Name)
            .ToListAsync(ct);

        if (chain.Count > 0)
        {
            _db.SecretRegistryEntries.RemoveRange(chain);
        }

        _db.SecretRegistryEntries.Add(new SecretRegistryEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Scope = @ref.Scope,
            OwnerId = @ref.OwnerId,
            Name = @ref.Name,
            StoreKey = storeKey,
            Origin = origin,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<SecretPointer?> LookupAsync(SecretRef @ref, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);

        var entry = await LoadLatestAsync(@ref, ct);
        return entry is null ? null : new SecretPointer(entry.StoreKey, entry.Origin);
    }

    /// <inheritdoc />
    public async Task<string?> LookupStoreKeyAsync(SecretRef @ref, CancellationToken ct)
    {
        var pointer = await LookupAsync(@ref, ct);
        return pointer?.StoreKey;
    }

    /// <inheritdoc />
    public Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(SecretRef @ref, CancellationToken ct)
        => LookupWithVersionAsync(@ref, version: null, ct);

    /// <inheritdoc />
    public async Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(
        SecretRef @ref, int? version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);

        var entry = version is null
            ? await LoadLatestAsync(@ref, ct)
            : await LoadSpecificVersionAsync(@ref, version.Value, ct);

        if (entry is null)
        {
            return null;
        }

        return (new SecretPointer(entry.StoreKey, entry.Origin), entry.Version);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, string ownerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var tenant = _tenantContext.CurrentTenantId;

        // Collapse per-version rows to one entry per (scope, owner,
        // name) triple. EF Core cannot GroupBy-then-project cleanly
        // against the in-memory provider used in tests, so pull the
        // distinct names and rehydrate refs client-side.
        var names = await _db.SecretRegistryEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenant && e.Scope == scope && e.OwnerId == ownerId)
            .Select(e => e.Name)
            .Distinct()
            .ToListAsync(ct);

        return names
            .Select(n => new SecretRef(scope, ownerId, n))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretVersionInfo>> ListVersionsAsync(SecretRef @ref, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);

        var tenant = _tenantContext.CurrentTenantId;

        var versions = await _db.SecretRegistryEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenant
                     && e.Scope == @ref.Scope
                     && e.OwnerId == @ref.OwnerId
                     && e.Name == @ref.Name)
            .OrderByDescending(e => e.Version)
            .Select(e => new { e.Version, e.Origin, e.CreatedAt })
            .ToListAsync(ct);

        if (versions.Count == 0)
        {
            return Array.Empty<SecretVersionInfo>();
        }

        // The row with the highest Version is the current one. Legacy
        // rows with a null Version never reached multi-version land;
        // they're treated as version 0 for ordering so a subsequent
        // rotate produces version 1. We do not surface null-version
        // rows in the per-version listing — callers that want them
        // should inspect the raw table, and the migration should have
        // made them moot.
        var materialized = versions
            .Where(v => v.Version.HasValue)
            .Select(v => new { Version = v.Version!.Value, v.Origin, v.CreatedAt })
            .ToList();

        if (materialized.Count == 0)
        {
            return Array.Empty<SecretVersionInfo>();
        }

        var currentVersion = materialized.Max(v => v.Version);

        return materialized
            .Select(v => new SecretVersionInfo(
                v.Version,
                v.Origin,
                v.CreatedAt,
                v.Version == currentVersion))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SecretRotation> RotateAsync(
        SecretRef @ref,
        string newStoreKey,
        SecretOrigin newOrigin,
        Func<string, CancellationToken, Task>? deletePreviousStoreKeyAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);
        ArgumentException.ThrowIfNullOrWhiteSpace(newStoreKey);
        _ = deletePreviousStoreKeyAsync; // retained for signature compat; never invoked under multi-version.

        var tenant = _tenantContext.CurrentTenantId;

        var latest = await LoadLatestAsync(@ref, ct);
        if (latest is null)
        {
            // Rotation requires an existing chain; callers that intend
            // to create go through RegisterAsync / POST. Surfacing this
            // as InvalidOperationException lets the endpoint layer map
            // it to 404 without parsing error strings.
            throw new InvalidOperationException(
                $"Cannot rotate secret '{@ref.Name}' for {@ref.Scope} '{@ref.OwnerId}': no registry entry exists in tenant '{tenant}'.");
        }

        var previousPointer = new SecretPointer(latest.StoreKey, latest.Origin);
        var fromVersion = latest.Version;
        var toVersion = (fromVersion ?? 0) + 1;

        // APPEND a new version. The old row stays in place so pinned
        // resolves (by SecretRef, version = fromVersion) continue to
        // work. Store-layer slots are reclaimed only by prune/delete.
        _db.SecretRegistryEntries.Add(new SecretRegistryEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Scope = @ref.Scope,
            OwnerId = @ref.OwnerId,
            Name = @ref.Name,
            StoreKey = newStoreKey,
            Origin = newOrigin,
            Version = toVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct);

        return new SecretRotation(
            @ref,
            fromVersion,
            toVersion,
            previousPointer,
            new SecretPointer(newStoreKey, newOrigin),
            PreviousStoreKeyDeleted: false);
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(
        SecretRef @ref,
        int keep,
        Func<string, CancellationToken, Task>? deletePrunedStoreKeyAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);
        if (keep < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep,
                "keep must be at least 1; prune never removes the current version.");
        }

        var tenant = _tenantContext.CurrentTenantId;

        var chain = await _db.SecretRegistryEntries
            .Where(e => e.TenantId == tenant
                     && e.Scope == @ref.Scope
                     && e.OwnerId == @ref.OwnerId
                     && e.Name == @ref.Name
                     && e.Version != null)
            .OrderByDescending(e => e.Version)
            .ToListAsync(ct);

        if (chain.Count <= keep)
        {
            return 0;
        }

        // Keep the top-N most recent (by version). Everything beyond
        // the first `keep` rows is pruned.
        var toRemove = chain.Skip(keep).ToList();
        _db.SecretRegistryEntries.RemoveRange(toRemove);
        await _db.SaveChangesAsync(ct);

        // Reclaim the store-layer slots for platform-owned pruned
        // versions. External-reference slots are never touched — the
        // customer owns them. Delete failures are swallowed in the
        // outer caller's fashion; we surface them here so callers can
        // decide.
        if (deletePrunedStoreKeyAsync is not null)
        {
            foreach (var row in toRemove)
            {
                if (row.Origin == SecretOrigin.PlatformOwned)
                {
                    await deletePrunedStoreKeyAsync(row.StoreKey, ct);
                }
            }
        }

        return toRemove.Count;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(SecretRef @ref, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);

        var tenant = _tenantContext.CurrentTenantId;

        var chain = await _db.SecretRegistryEntries
            .Where(e => e.TenantId == tenant
                     && e.Scope == @ref.Scope
                     && e.OwnerId == @ref.OwnerId
                     && e.Name == @ref.Name)
            .ToListAsync(ct);

        if (chain.Count == 0)
        {
            return;
        }

        _db.SecretRegistryEntries.RemoveRange(chain);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<SecretRegistryEntry?> LoadLatestAsync(SecretRef @ref, CancellationToken ct)
    {
        var tenant = _tenantContext.CurrentTenantId;

        // The EF Core in-memory provider (used by the test suite) does
        // not support sub-query projections like
        // `.Where(... Version == MAX(Version))` reliably; pulling the
        // small chain and picking the max client-side is both simpler
        // and provider-agnostic. Production chains are short (single
        // digits before prune in any realistic use case), so the
        // round-trip cost is negligible.
        return await _db.SecretRegistryEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenant
                     && e.Scope == @ref.Scope
                     && e.OwnerId == @ref.OwnerId
                     && e.Name == @ref.Name)
            .OrderByDescending(e => e.Version)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<SecretRegistryEntry?> LoadSpecificVersionAsync(
        SecretRef @ref, int version, CancellationToken ct)
    {
        var tenant = _tenantContext.CurrentTenantId;

        return await _db.SecretRegistryEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenant
                  && e.Scope == @ref.Scope
                  && e.OwnerId == @ref.OwnerId
                  && e.Name == @ref.Name
                  && e.Version == version,
                ct);
    }
}