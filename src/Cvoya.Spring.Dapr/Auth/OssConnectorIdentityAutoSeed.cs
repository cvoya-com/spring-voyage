// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default OSS <see cref="IConnectorIdentityAutoSeed"/> implementation
/// (#2408). Resolves the operator's stable human UUID from the ambient
/// HTTP request via <see cref="IHumanIdentityResolver"/> and upserts a
/// row in <c>human_connector_identities</c>. The hosted overlay swaps
/// this for tenant-aware multi-human resolution (#2411).
/// </summary>
/// <remarks>
/// Idempotent: a re-run with the same <c>(tenant, connectorId, userId)</c>
/// tuple is a no-op. When another human already owns the tuple, the seed
/// is silently skipped so the binding-config write path keeps the
/// existing mapping intact (the operator can resolve the conflict via
/// <c>spring human identity remove</c>). Calls outside of an HTTP request
/// (workflow / startup paths) skip the seed rather than throwing — the
/// auto-seed is opportunistic UX, not a correctness invariant.
/// </remarks>
internal sealed class OssConnectorIdentityAutoSeed(
    IHumanIdentityResolver identityResolver,
    SpringDbContext db,
    ILogger<OssConnectorIdentityAutoSeed> logger,
    IHttpContextAccessor? httpContextAccessor = null) : IConnectorIdentityAutoSeed
{
    /// <inheritdoc />
    public async Task SeedForCallerAsync(
        string connectorId,
        string connectorUserId,
        string? displayHandle = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectorId) || string.IsNullOrWhiteSpace(connectorUserId))
        {
            return;
        }

        var slug = connectorId.Trim();
        var userId = connectorUserId.Trim();

        var user = httpContextAccessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            // Non-HTTP / unauthenticated call sites are out of scope for
            // OSS auto-seed. The CLI verb path is the explicit channel.
            logger.LogDebug(
                "Connector identity auto-seed skipped: no authenticated caller on HTTP context.");
            return;
        }

        var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(claim))
        {
            logger.LogDebug(
                "Connector identity auto-seed skipped: NameIdentifier claim is empty.");
            return;
        }

        Guid humanId;
        try
        {
            humanId = await identityResolver.ResolveByUsernameAsync(claim, displayName: null, cancellationToken);
        }
        catch (Exception ex)
        {
            // Mirrors the #2404 workaround for #2405 — the resolver upserts
            // a row, so any failure here is best-effort skip rather than
            // a hard fail on the binding write.
            logger.LogWarning(ex,
                "Connector identity auto-seed: failed to resolve caller username '{Username}' to a human UUID; skipping.",
                claim);
            return;
        }

        var existing = await db.HumanConnectorIdentities
            .FirstOrDefaultAsync(
                e => e.ConnectorId == slug && e.ConnectorUserId == userId,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.HumanId != humanId)
            {
                // Another human already claims this connector identity.
                // Don't break the binding-write path; the operator resolves
                // via `spring human identity remove`.
                logger.LogInformation(
                    "Connector identity auto-seed skipped: '{Slug}:{User}' is already mapped to a different human.",
                    slug, userId);
                return;
            }

            // Same human → opportunistically refresh display_handle.
            var handle = string.IsNullOrWhiteSpace(displayHandle) ? existing.DisplayHandle : displayHandle.Trim();
            if (!string.Equals(handle, existing.DisplayHandle, StringComparison.Ordinal))
            {
                existing.DisplayHandle = handle;
                await db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var row = new HumanConnectorIdentityEntity
        {
            Id = Guid.NewGuid(),
            HumanId = humanId,
            ConnectorId = slug,
            ConnectorUserId = userId,
            DisplayHandle = string.IsNullOrWhiteSpace(displayHandle) ? null : displayHandle.Trim(),
        };

        try
        {
            db.HumanConnectorIdentities.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Auto-seeded connector identity '{Slug}:{User}' → human {HumanId} from binding write.",
                slug, userId, humanId);
        }
        catch (DbUpdateException ex)
        {
            // Race: a concurrent writer landed the same tuple. The unique
            // index caught it; detach and exit silently — the row exists.
            db.Entry(row).State = EntityState.Detached;
            logger.LogDebug(ex,
                "Connector identity auto-seed: '{Slug}:{User}' was already inserted by a concurrent writer.",
                slug, userId);
        }
    }
}
