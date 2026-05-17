// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
// IUnitMembershipRepository is resolved from a scope at runtime (not
// injected) because this provider is singleton and the repo is scoped.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reads agent definitions from <see cref="SpringDbContext.AgentDefinitions"/>
/// and projects them into the <see cref="AgentDefinition"/> shape consumed
/// by the execution layer. Extracts the agent's <c>execution</c> block,
/// then (B-wide, #601 / #603 / #409) merges it with the unit-level default
/// block persisted on the unit's <c>UnitDefinitions.Definition</c> JSON.
/// </summary>
/// <remarks>
/// <para>
/// Resolution chain per field (<c>image</c>, <c>runtime</c>, <c>provider</c>,
/// <c>model</c>, <c>agent</c>): <b>agent wins → unit default → null</b>.
/// <see cref="AgentHostingMode"/> is always agent-owned — a unit cannot
/// change whether an agent is ephemeral or persistent.
/// </para>
/// <para>
/// #1732: the launcher is selected from the <c>agent</c> slot's
/// catalogue runtime entry — <c>agent.Agent → unit.Agent → null</c>.
/// The execution tool is no longer threaded through the manifest /
/// DTOs / persistence; it is derived from the catalogue runtime's
/// <c>Launcher</c> field at dispatch time.
/// </para>
/// <para>
/// Tolerance: a missing unit membership, a missing unit execution block,
/// or a failed unit lookup surfaces as <c>null</c> for the unit side of
/// the merge; the dispatcher then sees the agent's declared value alone
/// and fails cleanly at save / dispatch time when a required field is
/// missing on both.
/// </para>
/// </remarks>
public class DbAgentDefinitionProvider(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IUnitExecutionStore? unitExecutionStore = null,
    IUnitStateCoordinator? unitStateCoordinator = null) : IAgentDefinitionProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbAgentDefinitionProvider>();

    /// <inheritdoc />
    public async Task<AgentDefinition?> GetByIdAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(agentId, out var agentUuid))
        {
            _logger.LogDebug("Agent id {AgentId} is not a valid Guid", agentId);
            return null;
        }

        var entity = await db.AgentDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agentUuid && a.DeletedAt == null, cancellationToken);

        if (entity is null)
        {
            // ADR-0039: units are agents. The dispatcher resolves a message
            // recipient by id without knowing whether it's an `agent://` or
            // a `unit://`. When the id does not match an agent definition
            // row, fall through to the unit-definitions table and project
            // the unit's own execution block as an AgentDefinition so the
            // dispatcher can launch the unit's runtime container directly.
            // Issue #2081 follow-up: before the reentrancy fix the dispatch
            // never reached this point (the actor-self-call deadlocked
            // first); now it does, and the unit-as-agent path needs an
            // actual implementation.
            var unitDefinition = await TryProjectUnitAsync(db, agentUuid, cancellationToken);
            if (unitDefinition is not null)
            {
                return unitDefinition;
            }

            _logger.LogDebug("No agent or unit definition found for id {Id}", agentId);
            return null;
        }

        var projected = Project(entity);

        // Resolve the agent's primary parent unit (first membership by
        // CreatedAt — same rule as AgentMetadata.ParentUnit) so we can
        //   (a) stamp UnitId on the projected definition for the credential
        //       resolver to consume at unit / parent-chain scope (#2251), and
        //   (b) merge the unit's execution defaults onto the agent's block
        //       when a unit-execution store is registered (#601 / #603 / #409).
        // The lookup is best-effort: a missing repo, an empty membership
        // list, or a failure all leave UnitId null and the merge unchanged.
        Guid? parentUnitId = null;
        try
        {
            var membershipRepo = scope.ServiceProvider
                .GetService<IUnitMembershipRepository>();
            if (membershipRepo is not null)
            {
                var memberships = await membershipRepo
                    .ListByAgentAsync(agentUuid, cancellationToken);
                if (memberships.Count > 0)
                {
                    parentUnitId = memberships[0].UnitId;
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: a failed membership lookup must not break dispatch;
            // it only narrows the credential resolver's search scope.
            _logger.LogWarning(ex,
                "Failed to resolve parent unit for agent {AgentId}; " +
                "credential resolution will skip unit / parent-chain scopes.",
                agentId);
        }

        if (parentUnitId is { } unitGuid)
        {
            projected = projected with
            {
                UnitId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitGuid),
            };

            // B-wide (#601): merge the parent unit's execution defaults onto
            // the agent's block when a unit-execution store is registered.
            if (unitExecutionStore is not null)
            {
                try
                {
                    var unitDefaults = await unitExecutionStore
                        .GetAsync(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitGuid), cancellationToken);
                    if (unitDefaults is not null)
                    {
                        var merged = Merge(projected.Execution, unitDefaults);
                        // Assign (not return) so a Merge result with no runtime id can still
                        // fall through to the unit-definitions fallback below (#2208).
                        projected = projected with { Execution = merged };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to resolve unit-level execution defaults for agent {AgentId}; " +
                        "continuing with agent-only configuration.",
                        agentId);
                }
            }
        }

        if (projected.Execution is null)
        {
            var unitDefinition = await TryProjectUnitAsync(db, agentUuid, cancellationToken);
            if (unitDefinition is not null)
            {
                return unitDefinition;
            }
        }

        return projected;
    }

    internal static AgentDefinition Project(AgentDefinitionEntity entity)
    {
        string? instructions = null;
        AgentExecutionConfig? execution = null;

        if (entity.Definition is { ValueKind: JsonValueKind.Object } definition)
        {
            if (definition.TryGetProperty("instructions", out var instructionsProp) &&
                instructionsProp.ValueKind == JsonValueKind.String)
            {
                instructions = instructionsProp.GetString();
            }

            execution = ExtractExecution(definition);
        }

        return new AgentDefinition(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entity.Id),
            entity.DisplayName,
            instructions,
            execution);
    }

    /// <summary>
    /// Projects a <c>UnitDefinitionEntity</c> as an <see cref="AgentDefinition"/>
    /// so the dispatcher can launch the unit's own runtime container (per
    /// ADR-0039 — units are agents). Returns <c>null</c> when no unit row
    /// exists for <paramref name="unitId"/>, or when the row was soft-deleted.
    /// When the unit row exists but its definition JSON does not carry an
    /// executable runtime slot (<c>execution.agent</c>), the returned
    /// definition has <c>null</c> execution so the dispatcher can surface a
    /// precise configuration error instead of pretending the unit id was
    /// unknown.
    /// </summary>
    /// <remarks>
    /// Hosting is read from <c>unit_live_config</c> via the
    /// <see cref="IUnitStateCoordinator"/> overlay block below (#2086 approach
    /// b). The JSON snapshot does not carry hosting; the live store is
    /// authoritative. The unit's <c>execution</c> JSON shape is identical to
    /// the agent's, so <see cref="ExtractExecution"/> handles both — we just
    /// reuse it. Instructions come from the unit's top-level
    /// <c>instructions</c> field when present (mirrors agent definitions).
    /// </remarks>
    private async Task<AgentDefinition?> TryProjectUnitAsync(
        SpringDbContext db,
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var unit = await db.UnitDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == unitId && u.DeletedAt == null, cancellationToken);

        if (unit is null)
        {
            return null;
        }

        string? instructions = null;
        AgentExecutionConfig? execution = null;

        if (unit.Definition is { ValueKind: JsonValueKind.Object } definition)
        {
            if (definition.TryGetProperty("instructions", out var instructionsProp) &&
                instructionsProp.ValueKind == JsonValueKind.String)
            {
                instructions = instructionsProp.GetString();
            }

            execution = ExtractExecution(definition);
        }

        // Overlay live-config slots (unit_live_config) on top of the JSON-
        // derived execution. The unit-create flow writes Hosting / Color /
        // Model / Provider to unit_live_config via UnitActor.SetMetadataAsync
        // — but does NOT round-trip them into unit_definitions.definition
        // (the JSON is the at-create snapshot only). For dispatch we want
        // the authoritative live values, which is what every other
        // metadata reader returns. Most critically for #2081/#2082
        // follow-up: a unit created with `hosting: persistent` was being
        // dispatched as Ephemeral because the JSON didn't carry the flag.
        if (execution is not null && unitStateCoordinator is not null)
        {
            try
            {
                var unitIdString = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unit.Id);
                var metadata = await unitStateCoordinator.GetMetadataAsync(unitIdString, cancellationToken);

                var liveHosting = ParseHosting(metadata.Hosting);
                execution = execution with
                {
                    AgentRuntimeId = execution.AgentRuntimeId,
                    Image = execution.Image,
                    Hosting = metadata.Hosting is null ? execution.Hosting : liveHosting,
                    Provider = metadata.Provider ?? execution.Provider,
                    Model = metadata.Model ?? execution.Model,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to overlay unit_live_config onto unit {UnitId}'s execution; falling back to JSON-only values.",
                    unitId);
            }
        }

        // Deliberate null-Execution path (#2208): when the unit row exists but
        // its JSON has no `execution.agent` runtime slot, return a definition
        // with Execution: null so the dispatcher can surface a precise
        // "no execution configuration" error instead of a misleading
        // "subject not found" 404.
        //
        // UnitId mirrors the unit's own id — a unit-as-agent (ADR-0039) is
        // its own owning scope for credential resolution (#2251).
        return new AgentDefinition(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unit.Id),
            unit.DisplayName,
            instructions,
            execution,
            UnitId: Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unit.Id));
    }

    /// <summary>
    /// Merges an agent's declared execution config with its parent unit's
    /// <see cref="UnitExecutionDefaults"/>. Field-level precedence: agent
    /// non-null wins; otherwise unit non-null fills in; otherwise the
    /// resulting slot is <c>null</c> (the dispatcher / save-time validator
    /// decides whether that's fatal).
    /// </summary>
    /// <remarks>
    /// #1732: <see cref="AgentExecutionConfig.AgentRuntimeId"/> is sourced
    /// from <c>agent.Agent → unit.Agent → null</c>. The dispatcher passes
    /// the resulting value through
    /// <see cref="Cvoya.Spring.Core.Catalog.IRuntimeCatalog.GetAgentRuntime"/>
    /// to derive the launcher (via the catalogue runtime's
    /// <c>Launcher</c> field).
    /// </remarks>
    internal static AgentExecutionConfig? Merge(
        AgentExecutionConfig? agent,
        UnitExecutionDefaults unit)
    {
        // AgentRuntimeId is required to produce an AgentExecutionConfig at
        // all. Resolution: agent.AgentRuntimeId (non-empty) → unit.Agent
        // → null. The dispatcher derives the launcher from the runtime
        // registry, so without this slot there is no way to dispatch.
        var agentRuntimeId = FirstNonBlank(agent?.AgentRuntimeId, unit.Agent);
        if (string.IsNullOrWhiteSpace(agentRuntimeId))
        {
            return null;
        }

        return new AgentExecutionConfig(
            AgentRuntimeId: agentRuntimeId,
            Image: FirstNonBlank(agent?.Image, unit.Image),
            // Hosting mode is agent-owned. Default (Persistent) when the
            // agent has no execution block at all (#2085).
            Hosting: agent?.Hosting ?? AgentHostingMode.Persistent,
            Provider: FirstNonBlank(agent?.Provider, unit.Provider),
            Model: FirstNonBlank(agent?.Model, unit.Model));
    }

    private static string? FirstNonBlank(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first)) return first.Trim();
        if (!string.IsNullOrWhiteSpace(second)) return second.Trim();
        return null;
    }

    private static AgentExecutionConfig? ExtractExecution(JsonElement definition)
    {
        // Preferred: top-level `execution: { agent, image, hosting, provider, model }`.
        // #1732: 'execution.tool' on persisted JSON is silently ignored — the
        // tool kind is derived from 'agent' (the runtime registry id) at
        // dispatch time. Pre-#1732 'execution.tool' values are not back-mapped
        // because the runtime id is the durable identity.
        if (definition.TryGetProperty("execution", out var exec) &&
            exec.ValueKind == JsonValueKind.Object)
        {
            var agentRuntimeId = GetStringOrNull(exec, "agent");
            var image = GetStringOrNull(exec, "image");
            var hosting = ParseHosting(GetStringOrNull(exec, "hosting"));
            var provider = GetStringOrNull(exec, "provider");
            var model = GetStringOrNull(exec, "model");

            if (agentRuntimeId is not null)
            {
                return new AgentExecutionConfig(agentRuntimeId, image, hosting, provider, model);
            }
        }

        // Legacy: `ai: { agent, environment: { image, runtime } }`. Same
        // rule — 'ai.tool' is ignored; 'ai.agent' is the durable id.
        if (definition.TryGetProperty("ai", out var ai) && ai.ValueKind == JsonValueKind.Object)
        {
            var agentRuntimeId = GetStringOrNull(ai, "agent");
            if (ai.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                var image = GetStringOrNull(env, "image");
                if (agentRuntimeId is not null && image is not null)
                {
                    return new AgentExecutionConfig(agentRuntimeId, image);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a stored <c>execution.hosting</c> literal onto the enum.
    /// </summary>
    /// <remarks>
    /// Issue #2436: the manifest parser now rejects unknown literals at
    /// parse time, so by the time the provider reads them off
    /// <c>AgentDefinitionEntity.Definition</c> JSON the value is
    /// guaranteed to be one of <c>persistent</c> / <c>ephemeral</c> /
    /// <c>pooled</c> (or null / whitespace). The silent-fallback branch
    /// that used to land any unknown literal on
    /// <see cref="AgentHostingMode.Persistent"/> is gone — any value
    /// that reaches this method that does not match the three known
    /// literals is a programming error (corrupted persisted JSON) and
    /// throws.
    /// </remarks>
    private static AgentHostingMode ParseHosting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AgentHostingMode.Persistent;
        }

        if (value.Equals("persistent", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Persistent;
        }

        if (value.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Ephemeral;
        }

        // "pooled" is reserved on the enum (PR 1 of #1087) for #362's
        // warm-pool dispatch model. The dispatcher rejects it at dispatch
        // time with NotSupportedException until #362 lands.
        if (value.Equals("pooled", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Pooled;
        }

        throw new InvalidOperationException(
            $"Persisted agent definition carries an unknown execution.hosting literal '{value}'. " +
            $"Manifest parsing should have rejected this value at install time (issue #2436); " +
            $"the persisted state has been corrupted out-of-band.");
    }

    private static string? GetStringOrNull(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
