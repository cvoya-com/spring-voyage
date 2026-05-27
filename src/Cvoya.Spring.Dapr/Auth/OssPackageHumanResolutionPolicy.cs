// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Packages;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// OSS default <see cref="IPackageHumanResolutionPolicy"/>. ADR-0046 §10
/// reshapes the policy: for each package <c>- human:</c> declaration it
/// mints a fresh <see cref="HumanEntity"/> row (Id = Guid.NewGuid()) with
/// a derived <c>DisplayName</c> (manifest value if set, otherwise
/// <c>"&lt;tenant-user-display-name&gt; · &lt;roles[0]&gt;"</c> or the
/// TenantUser's display name verbatim when no roles are declared) and a
/// synthetic <c>Username</c> ("oss-position-&lt;id&gt;"). The persisted row
/// is returned as <see cref="PackageHumanResolutionOutcome.Resolved"/> so
/// the activator can wire the <c>unit_memberships_humans</c> edge.
/// </summary>
/// <remarks>
/// <para>
/// Replaces ADR-0044's "auto-fill with install caller's UUID" behaviour.
/// The OSS dogfooding pattern is "every package-declared human is a distinct
/// operator-managed identity", which keeps the per-human Identity /
/// Connector / Config surfaces consistent across declarations. The
/// <c>{human_id → user_id}</c> mapping that would let a single physical
/// user fill multiple declarations is v0.2.
/// </para>
/// <para>
/// The cloud overlay pre-registers a hosted variant via the
/// <c>TryAddSingleton</c> seam; that registration wins and this default
/// never runs in the hosted deployment. The policy is a singleton — it
/// resolves a fresh scope per call so the scoped <see cref="SpringDbContext"/>
/// is available.
/// </para>
/// </remarks>
public sealed class OssPackageHumanResolutionPolicy(
    IServiceScopeFactory scopeFactory,
    ILogger<OssPackageHumanResolutionPolicy> logger) : IPackageHumanResolutionPolicy
{
    /// <inheritdoc />
    public async Task<PackageHumanResolution> ResolveAsync(
        PackageHumanResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rolesList = NormaliseList(request.Roles);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tenantUserDefaultResolver = scope.ServiceProvider.GetRequiredService<ITenantUserDefaultResolver>();

        // The synthetic username must be unique within the tenant per
        // HumanEntity's unique index on (tenant_id, username). Using the
        // freshly-minted Guid keeps it stable across retries and avoids
        // collisions with operator-created humans (whose usernames are JWT
        // subject claims, never of the "oss-position-..." shape).
        var humanId = Guid.NewGuid();
        var username = $"oss-position-{humanId:N}";

        // ADR-0062 § 1 / § 6 / #2822: the explicit per-declaration override
        // wins over the deployment default when supplied — the endpoint
        // validates the id exists in the current tenant before reaching
        // this policy, so we can trust the value here. Absent the override
        // (the common path), call the resolver. OSS resolver: operator
        // literal. Cloud resolver: calling principal.
        var tenantUserId = request.ExplicitTenantUserId is { } overrideId
            ? overrideId
            : await tenantUserDefaultResolver
                .ResolveDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        // #2860: the Hat's display-name prefix derives from the bound
        // TenantUser's DisplayName so every Hat for the same operator
        // shares one prefix. Manifest-supplied displayName wins, then the
        // TenantUser's DisplayName + first role, then the TenantUser's
        // DisplayName verbatim when no roles are declared.
        var tenantUserDisplayName = await db.TenantUsers
            .AsNoTracking()
            .Where(u => u.Id == tenantUserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var displayName = DeriveDisplayName(request.DisplayName, rolesList, tenantUserDisplayName);

        var entity = new HumanEntity
        {
            Id = humanId,
            TenantUserId = tenantUserId,
            Username = username,
            DisplayName = displayName,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description!.Trim(),
            // PermissionLevel defaults to Operator — matches the existing
            // OSS unblock decision documented on HumanEntity (#1473 / #1479).
            // TenantId is stamped by SpringDbContext's audit hook from the
            // ambient ITenantContext on save.
        };

        try
        {
            db.Humans.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The unique index on (tenant_id, username) raced — astronomical
            // odds for a fresh Guid suffix, but treat it as a hard failure
            // so the install surface returns a precise diagnostic rather
            // than silently dropping the declaration.
            logger.LogWarning(ex,
                "OssPackageHumanResolutionPolicy: failed to insert HumanEntity for unit '{Unit}' " +
                "(roles=[{Roles}]); username '{Username}' collided.",
                request.UnitDisplayName, string.Join(", ", rolesList), username);
            db.Entry(entity).State = EntityState.Detached;
            return new PackageHumanResolution(
                PackageHumanResolutionOutcome.Rejected,
                Array.Empty<Guid>(),
                Reason: $"Failed to mint a fresh HumanEntity: {ex.Message}");
        }

        logger.LogInformation(
            "OssPackageHumanResolutionPolicy: minted HumanEntity {HumanId} ('{DisplayName}', roles=[{Roles}]) " +
            "for unit '{Unit}'.",
            humanId, displayName, string.Join(", ", rolesList), request.UnitDisplayName);

        return new PackageHumanResolution(
            PackageHumanResolutionOutcome.Resolved,
            new[] { humanId });
    }

    /// <summary>
    /// Derives the <c>DisplayName</c> per ADR-0046 §7 + #2860: the manifest
    /// value wins when set; otherwise the bound TenantUser's
    /// <c>DisplayName</c> is used as the prefix (so every Hat bound to the
    /// same TenantUser shares one prefix in the portal), with the first
    /// role appended when one is declared. Falls back to the literal
    /// <c>"Operator"</c> only when the TenantUser row is missing or its
    /// <c>DisplayName</c> is empty — defensive guard for malformed seed
    /// states; the seeder always populates the column.
    /// </summary>
    private static string DeriveDisplayName(
        string? manifestDisplayName,
        IReadOnlyList<string> roles,
        string? tenantUserDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(manifestDisplayName))
        {
            return manifestDisplayName!.Trim();
        }
        var prefix = string.IsNullOrWhiteSpace(tenantUserDisplayName)
            ? "Operator"
            : tenantUserDisplayName!.Trim();
        if (roles.Count > 0)
        {
            return $"{prefix} · {roles[0]}";
        }
        return prefix;
    }

    private static List<string> NormaliseList(IReadOnlyList<string>? raw)
    {
        if (raw is null) return new List<string>();
        var result = new List<string>(raw.Count);
        foreach (var s in raw)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            result.Add(s.Trim());
        }
        return result;
    }
}
