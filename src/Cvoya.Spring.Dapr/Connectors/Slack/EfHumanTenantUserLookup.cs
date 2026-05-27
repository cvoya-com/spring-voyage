// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors.Slack;

using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// EF-backed <see cref="IHumanTenantUserLookup"/>. Reads
/// <c>humans.tenant_user_id</c> (the explicit FK landed in
/// ADR-0062 §1). Singleton — opens a scope per call.
/// </summary>
public sealed class EfHumanTenantUserLookup : IHumanTenantUserLookup
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates a new <see cref="EfHumanTenantUserLookup"/>.</summary>
    public EfHumanTenantUserLookup(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<Guid?> GetTenantUserIdAsync(Guid humanId, CancellationToken cancellationToken = default)
    {
        if (humanId == Guid.Empty)
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var tenantUserId = await db.Humans
            .AsNoTracking()
            .Where(h => h.Id == humanId)
            .Select(h => (Guid?)h.TenantUserId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return tenantUserId;
    }
}
