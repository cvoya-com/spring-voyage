// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ILlmCredentialResolver"/> implementation. Walks the
/// canonical chain documented on
/// <see cref="ILlmCredentialResolver"/>:
/// Agent → Unit → Parent-unit chain → Tenant. Each tier delegates to the
/// existing <see cref="ISecretResolver"/> (which already implements the
/// Unit → Tenant inheritance fall-through) and to
/// <see cref="ISecretRegistry.LookupPropagateAsync"/> for the
/// per-secret <c>propagate</c> gate that controls ancestor-chain
/// inheritance (#1737).
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0038 §"Credential identity" (#1770), the canonical secret
/// name is computed inline via
/// <see cref="CredentialNaming.SecretNameFor"/> — the resolver no longer
/// reaches into a runtime registry. The <c>(provider, authMethod)</c>
/// pair is the cache key; the persisted slot is named
/// <c>{provider}-{authMethod-slug}</c>.
/// </para>
/// <para>
/// <b>Why the registry-level propagate flag matters.</b>
/// <see cref="ISecretResolver"/> already inherits a unit value from the
/// tenant when the unit row is missing — but the parent-unit chain walk
/// is shaped differently: an ancestor row exists, the question is
/// "does this ancestor's value propagate down?". Filtering happens at
/// this resolver layer, before the access-policy check, so an ancestor
/// row with <c>propagate = false</c> never even reaches the secret
/// store from a descendant's resolve.
/// </para>
/// </remarks>
public sealed class LlmCredentialResolver : ILlmCredentialResolver
{
    private const int MaxParentChainDepth = 32;

    private readonly ISecretResolver _secretResolver;
    private readonly ISecretRegistry _secretRegistry;
    private readonly IUnitSubunitMembershipRepository _unitSubunitRepository;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<LlmCredentialResolver> _logger;

    /// <summary>
    /// Creates a new <see cref="LlmCredentialResolver"/>.
    /// </summary>
    public LlmCredentialResolver(
        ISecretResolver secretResolver,
        ISecretRegistry secretRegistry,
        IUnitSubunitMembershipRepository unitSubunitRepository,
        ITenantContext tenantContext,
        ILogger<LlmCredentialResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(secretRegistry);
        ArgumentNullException.ThrowIfNull(unitSubunitRepository);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(logger);

        _secretResolver = secretResolver;
        _secretRegistry = secretRegistry;
        _unitSubunitRepository = unitSubunitRepository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LlmCredentialResolution> ResolveAsync(
        string providerId,
        AuthMethod authMethod,
        Guid? agentId,
        Guid? unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new LlmCredentialResolution(null, LlmCredentialSource.NotFound, string.Empty);
        }

        // ADR-0038 / #2161: credentials are keyed by provider and auth
        // method. Claude Code therefore resolves anthropic-oauth while
        // Spring Voyage Agent + Anthropic resolves anthropic-api-key.
        var secretName = CredentialNaming.SecretNameFor(providerId, authMethod);

        // A SecretUnreadableException at any tier means a slot exists but
        // its ciphertext did not authenticate — typically because the
        // at-rest encryption key rotated between the write and the read.
        // That is an operational state, not a crash: surface it as a
        // distinct LlmCredentialSource so the status endpoint can render
        // a well-formed "unreadable" response instead of returning 500.
        try
        {
            // Tier 0: Agent-scoped secret (#1737). Highest priority — a
            // per-agent override beats every unit / parent-unit / tenant
            // value. We use a Direct lookup at the agent scope; the
            // resolver does not fall through to anything else from agent
            // scope (only Unit → Tenant inheritance is implemented in the
            // composed resolver), so a NotFound here naturally drops to
            // tier 1.
            if (agentId.HasValue && agentId.Value != Guid.Empty)
            {
                var agentRef = new SecretRef(SecretScope.Agent, agentId.Value, secretName);
                var resolution = await _secretResolver.ResolveWithPathAsync(agentRef, cancellationToken);
                if (resolution.Value is { Length: > 0 } agentValue)
                {
                    return new LlmCredentialResolution(agentValue, LlmCredentialSource.Agent, secretName);
                }
            }

            // Tier 1: Unit-scoped secret (subject to the Unit → Tenant
            // inheritance fall-through implemented by
            // ComposedSecretResolver). When a unit id is supplied we ask
            // at unit scope so the resolver transparently inherits from
            // the tenant when the unit has no override; when no unit is
            // supplied we go straight to tenant scope. The tenant
            // fall-through wins ONLY when neither the unit chain nor any
            // propagating ancestor has a value (see tier 2 below).
            if (unitId.HasValue && unitId.Value != Guid.Empty)
            {
                var unitRef = new SecretRef(SecretScope.Unit, unitId.Value, secretName);
                var resolution = await _secretResolver.ResolveWithPathAsync(unitRef, cancellationToken);
                if (resolution.Value is { Length: > 0 } unitValue
                    && resolution.Path == SecretResolvePath.Direct)
                {
                    // Direct unit hit — the unit owns the value, no
                    // inheritance involved.
                    return new LlmCredentialResolution(unitValue, LlmCredentialSource.Unit, secretName);
                }

                // Either the unit row is missing entirely, or the
                // composed resolver fell through to the tenant. Before
                // we accept the tenant fall-through we must walk the
                // parent-unit chain — a propagating ancestor unit beats
                // the tenant default.
                var parentHit = await ResolveFromParentChainAsync(unitId.Value, secretName, cancellationToken);
                if (parentHit is not null)
                {
                    return parentHit;
                }

                // No ancestor value found — surface the tenant fall-
                // through (if the composed resolver produced one) or
                // NotFound otherwise.
                if (resolution.Value is { Length: > 0 } tenantValue
                    && resolution.Path == SecretResolvePath.InheritedFromTenant)
                {
                    return new LlmCredentialResolution(tenantValue, LlmCredentialSource.Tenant, secretName);
                }
            }
            else
            {
                // No unit in context — consult tenant-scoped secret
                // directly. Agent-scope was already tried above.
                var tenantRef = new SecretRef(
                    SecretScope.Tenant,
                    _tenantContext.CurrentTenantId,
                    secretName);
                var resolution = await _secretResolver.ResolveWithPathAsync(tenantRef, cancellationToken);
                if (resolution.Value is { Length: > 0 } tenantValue)
                {
                    return new LlmCredentialResolution(tenantValue, LlmCredentialSource.Tenant, secretName);
                }
            }
        }
        catch (SecretUnreadableException ex)
        {
            _logger.LogWarning(
                ex,
                "LLM credential for provider {Provider} ({AuthMethod}) is stored but could not be decrypted; returning Unreadable.",
                providerId, authMethod);
            return new LlmCredentialResolution(null, LlmCredentialSource.Unreadable, secretName);
        }

        _logger.LogDebug(
            "LLM credential for provider {Provider} ({AuthMethod}) not configured at agent, unit, parent-unit, or tenant scope; returning NotFound.",
            providerId, authMethod);
        return new LlmCredentialResolution(null, LlmCredentialSource.NotFound, secretName);
    }

    /// <summary>
    /// Walks the parent-unit containment chain looking for a propagating
    /// secret with the given name. Returns the first hit (closest
    /// ancestor wins) tagged as <see cref="LlmCredentialSource.ParentUnit"/>,
    /// or <c>null</c> if no ancestor in the chain holds a propagating
    /// value. Cycle protection is provided by a visited-set plus a hard
    /// depth bound — an unintentional unit graph cycle does not stall
    /// the resolver.
    /// </summary>
    private async Task<LlmCredentialResolution?> ResolveFromParentChainAsync(
        Guid startUnitId,
        string secretName,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { startUnitId };

        // We start by asking the projection for the parent of startUnitId.
        // The projection lists every direct parent edge; we walk only the
        // first parent (1:N containment) but tolerate the multi-parent
        // shape #217 envisages by visiting every parent in BFS order.
        var frontier = await _unitSubunitRepository.ListByChildAsync(startUnitId, cancellationToken);

        var depth = 0;
        var queue = new Queue<Guid>();
        foreach (var edge in frontier)
        {
            if (visited.Add(edge.ParentId))
            {
                queue.Enqueue(edge.ParentId);
            }
        }

        while (queue.Count > 0 && depth++ < MaxParentChainDepth)
        {
            var levelSize = queue.Count;
            for (var i = 0; i < levelSize; i++)
            {
                var ancestorId = queue.Dequeue();

                // Skip tenant-id parents (top-level units have the
                // tenant id as the parent edge in this projection); the
                // tenant fall-through is handled separately by the
                // resolver, not via this walk. We detect "tenant id"
                // by checking whether any unit-subunit row has the id
                // on the child side — if not, treat it as tenant-level
                // (terminal).
                var ancestorIsUnit = await IsUnitAsync(ancestorId, cancellationToken);

                if (ancestorIsUnit)
                {
                    var ancestorRef = new SecretRef(SecretScope.Unit, ancestorId, secretName);
                    var propagate = await _secretRegistry.LookupPropagateAsync(ancestorRef, cancellationToken);

                    if (propagate is true)
                    {
                        // The ancestor owns a propagating row — read
                        // through the resolver so RBAC + decryption +
                        // SecretUnreadableException semantics still
                        // apply. We force a direct read by passing
                        // the ancestor's exact ref; the composed
                        // resolver will only fall through to tenant
                        // when the row is missing, which would mean
                        // LookupPropagateAsync returned null above.
                        var resolution = await _secretResolver.ResolveWithPathAsync(ancestorRef, cancellationToken);
                        if (resolution.Value is { Length: > 0 } value
                            && resolution.Path == SecretResolvePath.Direct)
                        {
                            return new LlmCredentialResolution(value, LlmCredentialSource.ParentUnit, secretName);
                        }
                    }
                    // propagate == false: ancestor exists but is sealed
                    // — keep walking (a higher ancestor may still own a
                    // propagating row). propagate == null: no row at
                    // this scope, also keep walking.
                }

                // Continue walking up: collect the ancestor's parents.
                var parents = await _unitSubunitRepository.ListByChildAsync(ancestorId, cancellationToken);
                foreach (var edge in parents)
                {
                    if (visited.Add(edge.ParentId))
                    {
                        queue.Enqueue(edge.ParentId);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tells whether <paramref name="candidate"/> looks like a unit id
    /// (vs the tenant id used as a synthetic root parent in the
    /// containment projection). The cheapest test is "does anything point
    /// at this id as a child?" — top-level units have the tenant id as
    /// their parent and the tenant id never appears as a child.
    /// </summary>
    private async Task<bool> IsUnitAsync(Guid candidate, CancellationToken cancellationToken)
    {
        if (candidate == Guid.Empty || candidate == _tenantContext.CurrentTenantId)
        {
            return false;
        }

        var rows = await _unitSubunitRepository.ListByChildAsync(candidate, cancellationToken);
        // A unit always has a parent edge (either tenant or another
        // unit). Top-level units therefore have rows with the tenant
        // id as parent. We treat "no rows" as "this id is not a child
        // of anything in the projection" — which means it's the tenant
        // root, not a unit.
        return rows.Count > 0;
    }
}
