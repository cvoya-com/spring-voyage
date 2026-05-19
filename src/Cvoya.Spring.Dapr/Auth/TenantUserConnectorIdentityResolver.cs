// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default scoped <see cref="ITenantUserConnectorIdentityResolver"/>
/// backed by <see cref="SpringDbContext"/>'s
/// <c>tenant_user_connector_identities</c> DbSet (ADR-0047 §2). Tenant
/// scoping is provided by the DbContext query filter; no additional
/// tenant check is needed here.
/// </summary>
internal sealed class TenantUserConnectorIdentityResolver(SpringDbContext db) : ITenantUserConnectorIdentityResolver
{
    /// <inheritdoc />
    public async Task<TenantUserConnectorIdentity?> ResolveTenantUserByUsernameAsync(
        string connectorId,
        string username,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectorId) || string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var row = await db.TenantUserConnectorIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.ConnectorId == connectorId && e.Username == username,
                cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new TenantUserConnectorIdentity(
            row.TenantUserId,
            row.ConnectorId,
            row.Username,
            row.DisplayHandle);
    }

    /// <inheritdoc />
    public async Task<string?> GetUsernameAsync(
        Guid tenantUserId,
        string connectorId,
        CancellationToken cancellationToken = default)
    {
        if (tenantUserId == Guid.Empty || string.IsNullOrWhiteSpace(connectorId))
        {
            return null;
        }

        return await db.TenantUserConnectorIdentities
            .AsNoTracking()
            .Where(e => e.TenantUserId == tenantUserId && e.ConnectorId == connectorId)
            .Select(e => e.Username)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
