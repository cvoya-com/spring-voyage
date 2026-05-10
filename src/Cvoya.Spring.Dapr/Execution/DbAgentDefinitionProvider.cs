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

        // B-wide (#601): if a unit execution store is registered, look up
        // the agent's parent unit (first membership by CreatedAt — same
        // rule as AgentMetadata.ParentUnit) and merge its defaults.
        if (unitExecutionStore is not null)
        {
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
                        var unitId = memberships[0].UnitId;
                        var unitDefaults = await unitExecutionStore
                            .GetAsync(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitId), cancellationToken);
                        if (unitDefaults is not null)
                        {
                            var merged = Merge(projected.Execution, unitDefaults);
                            return projected with { Execution = merged };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: unit lookup is best-effort. The dispatcher's
                // fail-clean check still fires if a required field is
                // missing after the merge.
                _logger.LogWarning(ex,
                    "Failed to resolve unit-level execution defaults for agent {AgentId}; " +
                    "continuing with agent-only configuration.",
                    agentId);
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
    /// exists for <paramref name="unitId"/>, when the row was soft-deleted,
    /// or when the unit's definition JSON does not carry an executable
    /// runtime slot (<c>execution.agent</c>) — the dispatcher requires
    /// an agent-runtime id to know which launcher to invoke.
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

        if (execution is null)
        {
            _logger.LogWarning(
                "Unit {UnitId} matched by id but has no execution.agent slot; cannot dispatch as runtime.",
                unitId);
            return null;
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
        if (unitStateCoordinator is not null)
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

        return new AgentDefinition(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unit.Id),
            unit.DisplayName,
            instructions,
            execution);
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

    private static AgentHostingMode ParseHosting(string? value)
    {
        if (value is null)
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
        // warm-pool dispatch model. Accept it on the parser side so YAML
        // written against #362 round-trips through the provider; the
        // dispatcher rejects it at dispatch time with NotSupportedException
        // until #362 lands.
        if (value.Equals("pooled", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Pooled;
        }

        return AgentHostingMode.Persistent;
    }

    private static string? GetStringOrNull(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
