// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Diagnostics;
using System.Text.Json;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF-backed implementation of <see cref="IAgentCloningPolicyRepository"/>
/// (#2051 / ADR-0040). Replaces the pre-ADR
/// <c>StateStoreAgentCloningPolicyRepository</c>: cloning policy is now
/// configuration data (operator-edited, queried at request time, must
/// survive restarts) and ADR-0040 § 1 sends every such datum to the
/// tenant-scoped EF surface.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SpringDbContext"/> stamps <c>TenantId</c> from the
/// ambient <c>ITenantContext</c> on insert and applies the per-entity
/// tenant query filter on read so cross-tenant access is impossible at
/// the repository layer.
/// </para>
/// <para>
/// The interface uses a string <c>targetId</c> for both scopes; for
/// <see cref="CloningPolicyScope.Agent"/> the string is parsed to the
/// agent's stable Guid (the same Guid the directory and clone endpoints
/// use). For <see cref="CloningPolicyScope.Tenant"/> the target id is
/// ignored — the row is uniquely identified by the ambient tenant and
/// the <c>tenant</c> scope discriminator, and scope_id is stored as
/// <c>NULL</c>. This keeps the public Core interface stable while the
/// storage layer moves to EF.
/// </para>
/// <para>
/// Reads are wrapped in a <see cref="Stopwatch"/> + <c>LogDebug</c> so
/// activation latency is observable per ADR-0040 § 3 (the v0.2 cache
/// decision is data-driven).
/// </para>
/// </remarks>
public class EfAgentCloningPolicyRepository(
    SpringDbContext context,
    ILogger<EfAgentCloningPolicyRepository> logger) : IAgentCloningPolicyRepository
{
    /// <inheritdoc />
    public async Task<AgentCloningPolicy> GetAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var sw = Stopwatch.StartNew();
        var query = QueryFor(scope, targetId);
        if (query is null)
        {
            // Unparseable agent target id — treat as "no row", matching the
            // pre-ADR repository contract (GetAsync returns Empty rather
            // than throwing for unknown agents).
            return AgentCloningPolicy.Empty;
        }

        var row = await query
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        sw.Stop();
        logger.LogDebug(
            "CloningPolicy.Get scope={Scope} targetId={TargetId} present={Present} elapsedMs={ElapsedMs}",
            scope, targetId, row is not null, sw.Elapsed.TotalMilliseconds);

        if (row is null)
        {
            return AgentCloningPolicy.Empty;
        }

        return DeserialisePolicy(row.Policy);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        CloningPolicyScope scope,
        string targetId,
        AgentCloningPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(policy);

        // An all-null policy is "no constraint at this scope" — drop the
        // row so /GET returns Empty from the absent-row branch instead of
        // a persisted Empty payload (matches the pre-ADR contract).
        if (policy.IsEmpty)
        {
            await DeleteAsync(scope, targetId, cancellationToken);
            return;
        }

        var (scopeType, scopeId) = ResolveDiscriminator(scope, targetId);
        if (scopeType is null)
        {
            // Unparseable agent target id: refuse the write so a malformed
            // id surfaces immediately rather than as a silent no-op.
            throw new ArgumentException(
                $"Cloning policy target '{targetId}' is not a valid agent id.",
                nameof(targetId));
        }

        var row = await context.CloningPolicies
            .FirstOrDefaultAsync(
                e => e.ScopeType == scopeType && e.ScopeId == scopeId,
                cancellationToken);

        var payload = SerialisePolicy(policy);
        var now = DateTimeOffset.UtcNow;

        if (row is null)
        {
            // Fresh write — synthetic Id, TenantId stamped by the audit
            // pipeline.
            context.CloningPolicies.Add(new CloningPolicyEntity
            {
                Id = Guid.NewGuid(),
                ScopeType = scopeType,
                ScopeId = scopeId,
                Policy = payload,
                UpdatedAt = now,
            });
        }
        else
        {
            // Upsert in place so the row id stays stable (any future joins
            // on policy identity remain valid).
            row.Policy = payload;
            row.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var query = QueryFor(scope, targetId);
        if (query is null)
        {
            // Unparseable agent target id is a no-op delete (idempotency
            // contract: deleting a non-existent row never throws).
            return;
        }

        var row = await query.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return;
        }

        context.CloningPolicies.Remove(row);
        await context.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<CloningPolicyEntity>? QueryFor(CloningPolicyScope scope, string targetId)
    {
        var (scopeType, scopeId) = ResolveDiscriminator(scope, targetId);
        if (scopeType is null)
        {
            return null;
        }

        return context.CloningPolicies
            .Where(e => e.ScopeType == scopeType && e.ScopeId == scopeId);
    }

    private static (string? ScopeType, Guid? ScopeId) ResolveDiscriminator(
        CloningPolicyScope scope, string targetId) => scope switch
        {
            CloningPolicyScope.Agent => Guid.TryParse(targetId, out var guid)
                ? (CloningPolicyScopeType.Agent, (Guid?)guid)
                : (null, null),
            // Tenant scope: target id is ignored (tenant filter does the
            // scoping). scope_id is NULL on the row so the partial unique
            // index pins a single tenant-scope row per tenant.
            CloningPolicyScope.Tenant => (CloningPolicyScopeType.Tenant, (Guid?)null),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "Unknown cloning-policy scope."),
        };

    private static JsonElement SerialisePolicy(AgentCloningPolicy policy)
        => JsonSerializer.SerializeToElement(policy);

    private static AgentCloningPolicy DeserialisePolicy(JsonElement payload)
        => JsonSerializer.Deserialize<AgentCloningPolicy>(payload) ?? AgentCloningPolicy.Empty;
}
