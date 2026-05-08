// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Agents;

using System.Collections.Generic;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExecutionConfigInheritanceResolver"/> implementation
/// (ADR-0039 §6). Reads each parent unit's persisted execution defaults
/// through <see cref="IUnitExecutionStore"/>, then intersects them per
/// field against the agent's own (possibly partial) configuration.
/// </summary>
/// <remarks>
/// <para>
/// Field-level rules (per ADR-0039 §6):
/// </para>
/// <list type="number">
///   <item>An explicit value on <c>agentOwn</c> always wins (no inheritance
///   for that field; a non-null agent value is never reported as a
///   conflict).</item>
///   <item>For a field left to inherit (null on <c>agentOwn</c>): if every
///   parent's effective config agrees, the agent inherits that value; if
///   any parent diverges, the field is reported on
///   <see cref="InheritanceResolution.ConflictingFields"/> and the caller
///   rejects the operation.</item>
///   <item>Top-level agents (no parent unit) inherit from tenant
///   defaults — see the gap note below.</item>
/// </list>
/// <para>
/// <b>Hosting</b> is agent-owned per ADR-0039 §6 and is never inherited
/// from a parent unit. The resolver always carries
/// <see cref="AgentExecutionConfig.Hosting"/> from <c>agentOwn</c> through
/// to <see cref="InheritanceResolution.Effective"/>.
/// </para>
/// <para>
/// <b>Known gaps</b> (tracked as follow-ups; deliberately not addressed
/// in this PR per the A5 scope):
/// </para>
/// <list type="bullet">
///   <item>Tenant-default fallthrough for the zero-parent branch reads
///   only <c>agentOwn</c> today. The OSS host has no <c>ITenantDefaults</c>
///   abstraction yet; A5 returns <c>agentOwn</c> as-is when
///   <c>parentUnitIds</c> is empty so the cloud overlay can replace this
///   resolver via the standard <c>TryAdd*</c> seam without first
///   introducing a new core interface.</item>
///   <item>Each parent's "effective" config is read directly from
///   <see cref="IUnitExecutionStore"/> (the persisted block on the
///   unit's <c>UnitDefinitions.Definition</c> JSON). Multi-level
///   unit-to-unit inheritance — a parent unit that itself inherits from
///   <i>its</i> parent — is out of scope for A5; ADR-0039 §6 calls for it
///   on later phases of the plan and will be slotted in when the unit
///   definition provider gains an "effective config" read path.</item>
/// </list>
/// </remarks>
public class ExecutionConfigInheritanceResolver(
    IUnitExecutionStore unitExecutionStore,
    ILogger<ExecutionConfigInheritanceResolver> logger)
    : IExecutionConfigInheritanceResolver
{
    /// <summary>
    /// Field names returned on <see cref="InheritanceResolution.ConflictingFields"/>
    /// for the four inheritable slots. Hosting is excluded — it is
    /// agent-owned per ADR-0039 §6.
    /// </summary>
    /// <remarks>
    /// Names mirror the persisted JSON keys on the unit/agent
    /// <c>execution:</c> block (see <see cref="UnitExecutionDefaults"/> and
    /// <see cref="AgentExecutionShape"/>) so the 422 envelope the API
    /// surfaces map 1:1 with what the operator wrote.
    /// </remarks>
    public static class FieldNames
    {
        public const string Agent = "agent";
        public const string Image = "image";
        public const string Provider = "provider";
        public const string Model = "model";
    }

    /// <inheritdoc />
    public InheritanceResolution ResolveAgentConfig(
        AgentExecutionConfig agentOwn,
        IReadOnlyList<Guid> parentUnitIds,
        Guid tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentOwn);
        ArgumentNullException.ThrowIfNull(parentUnitIds);

        // Branch 1: zero parents (tenant-parented agent). ADR-0039 §6
        // calls for tenant-default fallthrough here; the OSS host has no
        // ITenantDefaults abstraction yet, so the resolver returns
        // agentOwn as-is and leaves any fail-clean check to save-time
        // validation. The cloud overlay can substitute a tenant-aware
        // resolver via the standard TryAdd* seam.
        if (parentUnitIds.Count == 0)
        {
            return new InheritanceResolution(
                agentOwn,
                new Dictionary<string, IReadOnlyList<ParentValue>>());
        }

        // Load each parent unit's persisted execution defaults. The store
        // is async; the resolver interface is synchronous (A3, ADR-0039 §6
        // signature). ASP.NET Core has no SynchronizationContext, so the
        // sync-over-async wait does not deadlock. If a future caller runs
        // on a context that does (e.g. a UI thread), revisit by making the
        // interface async — out of scope for A5.
        var parentDefaults = LoadParentDefaults(parentUnitIds, ct);

        // Intersect per inheritable field. Hosting is agent-owned and is
        // never inherited from a parent.
        var conflicts = new Dictionary<string, IReadOnlyList<ParentValue>>();

        var resolvedAgentRuntimeId = ResolveField(
            FieldNames.Agent,
            ownValue: NullIfBlank(agentOwn.AgentRuntimeId),
            parentValues: parentDefaults.Select(p => (p.UnitId, NullIfBlank(p.Defaults?.Agent))),
            conflicts);

        var resolvedImage = ResolveField(
            FieldNames.Image,
            ownValue: NullIfBlank(agentOwn.Image),
            parentValues: parentDefaults.Select(p => (p.UnitId, NullIfBlank(p.Defaults?.Image))),
            conflicts);

        var resolvedProvider = ResolveField(
            FieldNames.Provider,
            ownValue: NullIfBlank(agentOwn.Provider),
            parentValues: parentDefaults.Select(p => (p.UnitId, NullIfBlank(p.Defaults?.Provider))),
            conflicts);

        var resolvedModel = ResolveField(
            FieldNames.Model,
            ownValue: NullIfBlank(agentOwn.Model),
            parentValues: parentDefaults.Select(p => (p.UnitId, NullIfBlank(p.Defaults?.Model))),
            conflicts);

        // AgentRuntimeId is non-nullable on AgentExecutionConfig. When the
        // intersection leaves it unresolved (every contributor null) we
        // fall back to the empty string — mirroring the contract on
        // DbAgentDefinitionProvider.Merge, where save-time validation
        // is responsible for rejecting an empty runtime id.
        var effective = agentOwn with
        {
            AgentRuntimeId = resolvedAgentRuntimeId ?? string.Empty,
            Image = resolvedImage,
            Provider = resolvedProvider,
            Model = resolvedModel,
            // Hosting is agent-owned — pass through verbatim.
            Hosting = agentOwn.Hosting,
            ConcurrentThreads = agentOwn.ConcurrentThreads,
        };

        if (conflicts.Count > 0)
        {
            logger.LogDebug(
                "Inheritance conflict resolving agent config across {ParentCount} parents (tenant {TenantId}): {ConflictFields}",
                parentUnitIds.Count,
                tenantId,
                string.Join(", ", conflicts.Keys));
        }

        return new InheritanceResolution(effective, conflicts);
    }

    /// <summary>
    /// Loads each parent unit's persisted <see cref="UnitExecutionDefaults"/>
    /// in input order. A missing parent (no row, blank id) surfaces as a
    /// <c>null</c> defaults entry so the per-field intersection still sees
    /// the parent slot but treats every field as unset.
    /// </summary>
    private List<ParentDefaults> LoadParentDefaults(
        IReadOnlyList<Guid> parentUnitIds,
        CancellationToken ct)
    {
        var result = new List<ParentDefaults>(parentUnitIds.Count);
        foreach (var unitId in parentUnitIds)
        {
            var formatted = GuidFormatter.Format(unitId);
            // Sync-over-async: see comment in ResolveAgentConfig.
            UnitExecutionDefaults? defaults;
            try
            {
                defaults = unitExecutionStore.GetAsync(formatted, ct).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to load execution defaults for parent unit {UnitId}; treating as empty.",
                    formatted);
                defaults = null;
            }

            result.Add(new ParentDefaults(unitId, defaults));
        }
        return result;
    }

    /// <summary>
    /// Per-field intersection rule (ADR-0039 §6):
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item>If the agent set the field explicitly (<paramref name="ownValue"/>
    ///   non-null), agent wins. No inheritance, no conflict.</item>
    ///   <item>Otherwise collect every non-null parent value. If none
    ///   supplied a value, the field stays unset (null).</item>
    ///   <item>If every supplying parent agrees on the value (ordinal
    ///   string equality), inherit it.</item>
    ///   <item>Otherwise record one <see cref="ParentValue"/> per
    ///   contributing parent on <paramref name="conflicts"/> under
    ///   <paramref name="fieldName"/> and return null. Callers must
    ///   inspect <see cref="InheritanceResolution.ConflictingFields"/>
    ///   before consuming <see cref="InheritanceResolution.Effective"/>.</item>
    /// </list>
    /// </remarks>
    private static string? ResolveField(
        string fieldName,
        string? ownValue,
        IEnumerable<(Guid UnitId, string? Value)> parentValues,
        IDictionary<string, IReadOnlyList<ParentValue>> conflicts)
    {
        if (ownValue is not null)
        {
            return ownValue;
        }

        // Materialise contributing parents (non-null values only) once.
        var contributing = new List<ParentValue>();
        foreach (var (unitId, value) in parentValues)
        {
            if (value is null)
            {
                continue;
            }
            contributing.Add(new ParentValue(unitId, value));
        }

        if (contributing.Count == 0)
        {
            return null;
        }

        var first = contributing[0].Value;
        var diverged = false;
        for (var i = 1; i < contributing.Count; i++)
        {
            if (!string.Equals(contributing[i].Value, first, StringComparison.Ordinal))
            {
                diverged = true;
                break;
            }
        }

        if (diverged)
        {
            conflicts[fieldName] = contributing;
            return null;
        }

        return first;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ParentDefaults(Guid UnitId, UnitExecutionDefaults? Defaults);
}