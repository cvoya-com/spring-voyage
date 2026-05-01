// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Cloning;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using Microsoft.Extensions.Logging;

/// <summary>
/// OSS default <see cref="IAgentCloningPolicyEnforcer"/>. Walks agent-scoped
/// policy, then tenant-scoped policy; numeric caps collapse to the minimum
/// non-null value so a tenant ceiling cannot be relaxed by an agent-scoped
/// override.
/// </summary>
/// <remarks>
/// <para>
/// Unit-boundary honouring (PR #497). When the source agent is a member
/// of one or more units, the enforcer consults each unit's
/// <see cref="UnitBoundary"/> and denies the request when the requested
/// attachment mode would lift the clone outside that boundary — today
/// that means "source agent sits behind an <c>Opaque</c> boundary, but
/// the caller asked for <see cref="AttachmentMode.Detached"/>". A
/// detached clone is registered as a peer in the parent's unit, which is
/// fine when the parent's unit is transparent, but bleeds a new
/// addressable entity through an opaque wall when it is not. Rather than
/// quietly re-attach the clone (which would surprise the caller), the
/// enforcer surfaces the conflict as a deny so the operator can pick:
/// widen the boundary, or switch to <c>Attached</c>.
/// </para>
/// <para>
/// Registered via <c>TryAdd*</c> so the private cloud host can layer
/// audit logging or tenant-scoped caches by pre-registering a decorator.
/// A policy-store outage does <em>not</em> throw — the enforcer logs a
/// warning and returns <see cref="CloningPolicyDecision.AllowedUnconstrained"/>
/// so a misconfigured state component never silently drops clone
/// requests that would otherwise be legal.
/// </para>
/// </remarks>
public class DefaultAgentCloningPolicyEnforcer(
    IAgentCloningPolicyRepository repository,
    ITenantContext tenantContext,
    IUnitMembershipRepository membershipRepository,
    IUnitBoundaryStore boundaryStore,
    IStateStore stateStore,
    IDirectoryService directoryService,
    ILoggerFactory loggerFactory) : IAgentCloningPolicyEnforcer
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DefaultAgentCloningPolicyEnforcer>();

    /// <summary>
    /// Maximum depth the parent walk traverses before giving up. Matches
    /// the depth cap on unit-membership cycle detection (<see cref="UnitActor"/>)
    /// so a misconfigured parent loop is reported rather than silently looped.
    /// </summary>
    internal const int MaxDepthWalk = 64;

    /// <inheritdoc />
    public async Task<CloningPolicyDecision> EvaluateAsync(
        string sourceAgentId,
        CloningPolicy requestedPolicy,
        AttachmentMode requestedAttachmentMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAgentId);

        AgentCloningPolicy agentPolicy;
        AgentCloningPolicy tenantPolicy;
        try
        {
            agentPolicy = await repository.GetAsync(
                CloningPolicyScope.Agent, sourceAgentId, cancellationToken);
            tenantPolicy = await repository.GetAsync(
                CloningPolicyScope.Tenant, tenantContext.CurrentTenantId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load cloning policy for agent {AgentId}; allowing request",
                sourceAgentId);
            return CloningPolicyDecision.AllowedUnconstrained;
        }

        // Allowed-policy check: both scopes must accept the enum. A null
        // list at either scope is "no restriction here" — only a non-null
        // list at either level can deny.
        if (!IsAllowed(agentPolicy.AllowedPolicies, requestedPolicy)
            || !IsAllowed(tenantPolicy.AllowedPolicies, requestedPolicy))
        {
            return CloningPolicyDecision.Deny(
                "policy",
                $"Cloning policy '{requestedPolicy}' is not permitted for agent '{sourceAgentId}'.");
        }

        if (!IsAllowed(agentPolicy.AllowedAttachmentModes, requestedAttachmentMode)
            || !IsAllowed(tenantPolicy.AllowedAttachmentModes, requestedAttachmentMode))
        {
            return CloningPolicyDecision.Deny(
                "attachment",
                $"Attachment mode '{requestedAttachmentMode}' is not permitted for agent '{sourceAgentId}'.");
        }

        // Depth check — walks CloneIdentity.ParentAgentId back to a non-clone
        // root. Every hop increments depth; MaxDepth = 0 means "no recursive
        // cloning allowed at all". The cap is the tightest non-null across
        // scopes.
        var effectiveMaxDepth = Min(agentPolicy.MaxDepth, tenantPolicy.MaxDepth);
        if (effectiveMaxDepth is int depthCap)
        {
            var depth = await ComputeCloneDepthAsync(sourceAgentId, cancellationToken);
            // The new clone sits at (sourceDepth + 1). A source at depth 0
            // with cap 0 means "cannot clone a root agent" — intentional
            // "disable cloning entirely for this scope".
            if (depth + 1 > depthCap)
            {
                return CloningPolicyDecision.Deny(
                    "max-depth",
                    $"Cloning '{sourceAgentId}' would exceed the configured max depth ({depthCap}).");
            }
        }

        // Boundary rule: if the source agent sits in a unit with an Opaque
        // boundary, refuse Detached attachment — a detached clone is
        // registered as a peer in the parent's unit and so remains inside
        // the boundary, but an Opaque boundary explicitly means "no member
        // is visible from outside". Adding a new addressable clone under
        // the same scope mirrors the opacity issue onto a newly-minted
        // entity; the operator should either switch to Attached (so the
        // clone rolls up under the parent's boundary) or widen the
        // boundary before cloning.
        if (requestedAttachmentMode == AttachmentMode.Detached)
        {
            var boundaryConflict = await CheckBoundaryOpacityAsync(sourceAgentId, cancellationToken);
            if (boundaryConflict is string conflict)
            {
                return CloningPolicyDecision.Deny("boundary", conflict);
            }
        }

        return new CloningPolicyDecision(
            Allowed: true,
            ResolvedMaxClones: Min(agentPolicy.MaxClones, tenantPolicy.MaxClones),
            ResolvedBudget: MinDecimal(agentPolicy.Budget, tenantPolicy.Budget));
    }

    private static bool IsAllowed<T>(IReadOnlyList<T>? allowList, T candidate)
    {
        if (allowList is null)
        {
            return true;
        }

        // An explicit empty list means "deny everything" — the operator
        // opted in to the allow-list shape but hasn't whitelisted the
        // requested value. This is the standard semantics used by
        // SkillPolicy / ModelPolicy and keeps behaviour consistent across
        // the policy surfaces.
        return allowList.Contains(candidate);
    }

    private static int? Min(int? a, int? b) => (a, b) switch
    {
        (null, null) => null,
        (null, _) => b,
        (_, null) => a,
        _ => Math.Min(a!.Value, b!.Value),
    };

    private static decimal? MinDecimal(decimal? a, decimal? b) => (a, b) switch
    {
        (null, null) => null,
        (null, _) => b,
        (_, null) => a,
        _ => Math.Min(a!.Value, b!.Value),
    };

    private async Task<int> ComputeCloneDepthAsync(string agentId, CancellationToken cancellationToken)
    {
        var current = agentId;
        for (var depth = 0; depth < MaxDepthWalk; depth++)
        {
            var key = $"{current}:{StateKeys.CloneIdentity}";
            var identity = await stateStore.GetAsync<CloneIdentity>(key, cancellationToken);
            if (identity is null)
            {
                return depth;
            }

            current = identity.ParentAgentId;
            if (string.IsNullOrWhiteSpace(current))
            {
                return depth + 1;
            }
        }

        // Hit the safety cap — treat as "very deep" so the caller's
        // depth-check still denies, without hanging the request on a
        // malformed parent chain.
        _logger.LogWarning(
            "Clone-depth walk for agent {AgentId} exceeded {MaxDepth} hops; treating as saturated",
            agentId, MaxDepthWalk);
        return MaxDepthWalk;
    }

    private async Task<string?> CheckBoundaryOpacityAsync(string agentId, CancellationToken cancellationToken)
    {
        // agentId is the actor UUID string. Parse to Guid for the membership repo.
        if (!Guid.TryParse(agentId, out var agentUuid))
        {
            return null;
        }

        IReadOnlyList<UnitMembership> memberships;
        try
        {
            memberships = await membershipRepository.ListByAgentAsync(agentUuid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to list memberships for agent {AgentId} during boundary check; skipping",
                agentId);
            return null;
        }

        // Pre-load directory entries once for UUID→slug resolution.
        IReadOnlyList<Spring.Core.Directory.DirectoryEntry> allEntries;
        try
        {
            allEntries = await directoryService.ListAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to list directory entries during boundary check; skipping");
            return null;
        }

        foreach (var membership in memberships)
        {
            var unitUuidStr = membership.UnitId.ToString();

            // Resolve the unit UUID to its slug-based Address for the boundary store,
            // which uses the directory-service-resolved address (slug scheme).
            var unitEntry = allEntries.FirstOrDefault(
                e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
                  && string.Equals(e.ActorId, unitUuidStr, StringComparison.OrdinalIgnoreCase));

            if (unitEntry is null)
            {
                continue;
            }

            UnitBoundary boundary;
            try
            {
                boundary = await boundaryStore.GetAsync(unitEntry.Address, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Failed to read boundary for unit {UnitId} during cloning check; skipping",
                    unitUuidStr);
                continue;
            }

            if (HasOpaqueBoundary(boundary))
            {
                return $"Agent '{agentId}' is a member of unit '{unitEntry.Address.Path}' which has opaque " +
                    "boundary rules. A detached clone would surface outside that boundary; " +
                    "use --attachment-mode attached or widen the unit boundary first.";
            }
        }

        return null;
    }

    private static bool HasOpaqueBoundary(UnitBoundary boundary)
    {
        // UnitBoundary is a record with three nullable collections; "has
        // opacity rules" is the concrete trigger for the deny. Projection
        // / synthesis alone don't introduce an opaque wall — they rewrite
        // rather than hide.
        return boundary is not null
            && boundary.Opacities is { Count: > 0 };
    }
}