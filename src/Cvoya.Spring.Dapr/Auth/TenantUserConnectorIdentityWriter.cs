// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default scoped <see cref="ITenantUserConnectorIdentityWriter"/> backed
/// by <see cref="SpringDbContext"/>. Mirrors the upsert / 404 / 409 shape
/// of <c>TenantUserIdentityEndpoints.UpsertIdentityAsync</c> so the OAuth
/// callback's user-identity hook (ADR-0047 §13) and the HTTP endpoint
/// converge on the same write semantics.
/// </summary>
/// <remarks>
/// The DbContext's tenant query filter handles tenant scoping. A
/// <c>tenant_user_id</c> from another tenant surfaces as <c>null</c> on
/// the existence probe and the writer returns
/// <see cref="TenantUserConnectorIdentityUpsertOutcome.TenantUserNotFound"/>.
/// </remarks>
internal sealed class TenantUserConnectorIdentityWriter(SpringDbContext db) : ITenantUserConnectorIdentityWriter
{
    /// <inheritdoc />
    public async Task<TenantUserConnectorIdentityUpsertOutcome> UpsertAsync(
        Guid tenantUserId,
        string connectorId,
        string username,
        string? displayHandle,
        CancellationToken cancellationToken = default)
    {
        if (tenantUserId == Guid.Empty)
        {
            return TenantUserConnectorIdentityUpsertOutcome.TenantUserNotFound;
        }

        var trimmedConnector = (connectorId ?? string.Empty).Trim();
        var trimmedUsername = (username ?? string.Empty).Trim();
        var trimmedDisplay = string.IsNullOrWhiteSpace(displayHandle)
            ? null
            : displayHandle.Trim();

        if (string.IsNullOrWhiteSpace(trimmedConnector) || string.IsNullOrWhiteSpace(trimmedUsername))
        {
            // Empty inputs are treated as a benign "nothing to do" — the
            // OAuth callback path supplies these from the GitHub
            // user-info response so a blank login should never reach
            // here; if it does, fall through quietly rather than rewriting
            // the row with empty values.
            return TenantUserConnectorIdentityUpsertOutcome.Upserted;
        }

        var tenantUserExists = await db.TenantUsers
            .AsNoTracking()
            .AnyAsync(u => u.Id == tenantUserId, cancellationToken);
        if (!tenantUserExists)
        {
            return TenantUserConnectorIdentityUpsertOutcome.TenantUserNotFound;
        }

        // Mirror TenantUserIdentityEndpoints.UpsertIdentityAsync: a row for
        // the same (connector, username) owned by a different tenant user
        // surfaces as a conflict; one owned by the same tenant user merges
        // in place.
        var existingByLogin = await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.ConnectorId == trimmedConnector && e.Username == trimmedUsername,
                cancellationToken);

        if (existingByLogin is not null && existingByLogin.TenantUserId != tenantUserId)
        {
            return TenantUserConnectorIdentityUpsertOutcome.LoginAlreadyClaimed;
        }

        var existingByPair = existingByLogin ?? await db.TenantUserConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.TenantUserId == tenantUserId && e.ConnectorId == trimmedConnector,
                cancellationToken);

        if (existingByPair is not null)
        {
            existingByPair.Username = trimmedUsername;
            existingByPair.DisplayHandle = trimmedDisplay;
            await db.SaveChangesAsync(cancellationToken);
            return TenantUserConnectorIdentityUpsertOutcome.Upserted;
        }

        var row = new TenantUserConnectorIdentityEntity
        {
            Id = Guid.NewGuid(),
            TenantUserId = tenantUserId,
            ConnectorId = trimmedConnector,
            Username = trimmedUsername,
            DisplayHandle = trimmedDisplay,
        };

        try
        {
            db.TenantUserConnectorIdentities.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            return TenantUserConnectorIdentityUpsertOutcome.Upserted;
        }
        catch (DbUpdateException)
        {
            // A concurrent writer landed the same (connector, username)
            // tuple between SELECT and INSERT. Treat the same way the
            // endpoint does — surface the collision so the caller can
            // emit a soft warning instead of replaying the OAuth flow.
            db.Entry(row).State = EntityState.Detached;
            return TenantUserConnectorIdentityUpsertOutcome.LoginAlreadyClaimed;
        }
    }
}
