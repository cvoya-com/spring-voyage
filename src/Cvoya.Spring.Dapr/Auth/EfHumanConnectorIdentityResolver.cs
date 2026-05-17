// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default scoped <see cref="IHumanConnectorIdentityResolver"/> backed by
/// <see cref="SpringDbContext"/>'s <c>human_connector_identities</c> DbSet
/// (#2408). Tenant scoping is provided by the DbContext query filter; no
/// additional tenant check is needed here.
/// </summary>
internal sealed class EfHumanConnectorIdentityResolver(SpringDbContext db) : IHumanConnectorIdentityResolver
{
    /// <inheritdoc />
    public async Task<HumanConnectorIdentity?> ResolveHumanAsync(
        string connectorId,
        string connectorUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectorId) || string.IsNullOrWhiteSpace(connectorUserId))
        {
            return null;
        }

        var row = await db.HumanConnectorIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.ConnectorId == connectorId && e.ConnectorUserId == connectorUserId,
                cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new HumanConnectorIdentity(
            row.HumanId,
            row.ConnectorId,
            row.ConnectorUserId,
            row.DisplayHandle);
    }

    /// <inheritdoc />
    public async Task<string?> ResolveUserIdAsync(
        Guid humanId,
        string connectorId,
        CancellationToken cancellationToken = default)
    {
        if (humanId == Guid.Empty || string.IsNullOrWhiteSpace(connectorId))
        {
            return null;
        }

        return await db.HumanConnectorIdentities
            .AsNoTracking()
            .Where(e => e.HumanId == humanId && e.ConnectorId == connectorId)
            .Select(e => e.ConnectorUserId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
