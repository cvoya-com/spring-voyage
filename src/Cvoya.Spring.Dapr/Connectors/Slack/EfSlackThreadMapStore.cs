// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors.Slack;

using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// EF-backed <see cref="ISlackThreadMapStore"/> reading and writing
/// rows in the <c>slack_thread_ts</c> table (ADR-0061 §3 / #2818).
/// </summary>
/// <remarks>
/// <para>
/// Lifetime is <b>singleton</b>: the store opens a fresh
/// <see cref="IServiceScope"/> per call so the scoped
/// <see cref="SpringDbContext"/> resolves cleanly from singleton call
/// sites. Same pattern as <see cref="UnitConnectorBindingStore"/>.
/// </para>
/// <para>
/// The cross-tenant unique index on <c>(tenant_id, team_id,
/// slack_thread_ts)</c> guarantees the inbound reverse lookup
/// resolves to at most one mapping per workspace per tenant.
/// </para>
/// </remarks>
public sealed class EfSlackThreadMapStore : ISlackThreadMapStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates a new <see cref="EfSlackThreadMapStore"/>.</summary>
    public EfSlackThreadMapStore(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task RecordAsync(
        Guid svThreadId,
        Guid boundTenantUserId,
        string teamId,
        string slackChannelId,
        string slackThreadTs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slackChannelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slackThreadTs);
        if (svThreadId == Guid.Empty)
        {
            throw new ArgumentException("SV thread id must not be Guid.Empty.", nameof(svThreadId));
        }
        if (boundTenantUserId == Guid.Empty)
        {
            throw new ArgumentException("Bound TenantUser id must not be Guid.Empty.", nameof(boundTenantUserId));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Upsert: if a row already exists for the outbound key we
        // update the thread_ts in place. In practice the outbound
        // path only inserts on first parent post; idempotency means a
        // retry of the same first-post does not race.
        var existing = await db.SlackThreadStates
            .FirstOrDefaultAsync(
                e => e.SvThreadId == svThreadId
                    && e.BoundTenantUserId == boundTenantUserId
                    && e.TeamId == teamId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.SlackThreadTs = slackThreadTs;
            existing.SlackChannelId = slackChannelId;
        }
        else
        {
            db.SlackThreadStates.Add(new SlackThreadStateEntity
            {
                Id = Guid.NewGuid(),
                SvThreadId = svThreadId,
                BoundTenantUserId = boundTenantUserId,
                TeamId = teamId,
                SlackThreadTs = slackThreadTs,
                SlackChannelId = slackChannelId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SlackThreadMapping?> LookupOutboundAsync(
        Guid svThreadId,
        Guid boundTenantUserId,
        string teamId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.SlackThreadStates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.SvThreadId == svThreadId
                    && e.BoundTenantUserId == boundTenantUserId
                    && e.TeamId == teamId,
                cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToMapping(row);
    }

    /// <inheritdoc />
    public async Task<SlackThreadMapping?> LookupSvThreadAsync(
        string teamId,
        string slackThreadTs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slackThreadTs);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.SlackThreadStates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TeamId == teamId && e.SlackThreadTs == slackThreadTs,
                cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToMapping(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlackThreadMapping>> ListForBoundUserAsync(
        Guid boundTenantUserId,
        string teamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        if (boundTenantUserId == Guid.Empty)
        {
            throw new ArgumentException("Bound TenantUser id must not be Guid.Empty.", nameof(boundTenantUserId));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.SlackThreadStates
            .AsNoTracking()
            .Where(e => e.BoundTenantUserId == boundTenantUserId && e.TeamId == teamId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToMapping).ToList();
    }

    private static SlackThreadMapping ToMapping(SlackThreadStateEntity row) => new(
        SvThreadId: row.SvThreadId,
        BoundTenantUserId: row.BoundTenantUserId,
        TeamId: row.TeamId,
        SlackChannelId: row.SlackChannelId,
        SlackThreadTs: row.SlackThreadTs);
}
