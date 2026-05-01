// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
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
/// Resolution chain per field (<c>tool</c>, <c>image</c>, <c>runtime</c>,
/// <c>provider</c>, <c>model</c>): <b>agent wins → unit default → null</b>.
/// <see cref="AgentHostingMode"/> is always agent-owned — a unit cannot
/// change whether an agent is ephemeral or persistent.
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
    IDirectoryService? directoryService = null,
    IUnitExecutionStore? unitExecutionStore = null) : IAgentDefinitionProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbAgentDefinitionProvider>();

    /// <inheritdoc />
    public async Task<AgentDefinition?> GetByIdAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.AgentDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AgentId == agentId && a.DeletedAt == null, cancellationToken);

        if (entity is null)
        {
            _logger.LogDebug("No agent definition found for id {AgentId}", agentId);
            return null;
        }

        var projected = Project(entity);

        // B-wide (#601): if a unit execution store is registered, look up
        // the agent's parent unit (first membership by CreatedAt — same
        // rule as AgentMetadata.ParentUnit) and merge its defaults.
        // Membership repo is scoped so we resolve it from the fresh scope
        // above rather than constructor-injecting it (singleton ≠ scoped).
        // After #1492, UnitMembership.UnitId is a Guid; resolve to slug via
        // IDirectoryService for IUnitExecutionStore which is slug-keyed.
        if (unitExecutionStore is not null)
        {
            try
            {
                var membershipRepo = scope.ServiceProvider
                    .GetService<IUnitMembershipRepository>();
                if (membershipRepo is not null && Guid.TryParse(agentId, out var agentUuid))
                {
                    var memberships = await membershipRepo
                        .ListByAgentAsync(agentUuid, cancellationToken);
                    if (memberships.Count > 0 && directoryService is not null)
                    {
                        var unitUuidStr = memberships[0].UnitId.ToString();
                        // Resolve UUID → slug via directory service.
                        var allEntries = await directoryService.ListAllAsync(cancellationToken);
                        var unitEntry = allEntries.FirstOrDefault(
                            e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
                              && string.Equals(e.ActorId, unitUuidStr, StringComparison.OrdinalIgnoreCase));
                        if (unitEntry is not null)
                        {
                            var unitDefaults = await unitExecutionStore
                                .GetAsync(unitEntry.Address.Path, cancellationToken);
                            if (unitDefaults is not null)
                            {
                                var merged = Merge(projected.Execution, unitDefaults);
                                return projected with { Execution = merged };
                            }
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

        return new AgentDefinition(entity.AgentId, entity.Name, instructions, execution);
    }

    /// <summary>
    /// Merges an agent's declared execution config with its parent unit's
    /// <see cref="UnitExecutionDefaults"/>. Field-level precedence: agent
    /// non-null wins; otherwise unit non-null fills in; otherwise the
    /// resulting slot is <c>null</c> (the dispatcher / save-time validator
    /// decides whether that's fatal).
    /// </summary>
    internal static AgentExecutionConfig? Merge(
        AgentExecutionConfig? agent,
        UnitExecutionDefaults unit)
    {
        // Tool is required to produce an AgentExecutionConfig at all.
        // Resolution: agent.Tool (non-empty) → unit.Tool → null.
        var tool = FirstNonBlank(agent?.Tool, unit.Tool);
        if (string.IsNullOrWhiteSpace(tool))
        {
            return null;
        }

        return new AgentExecutionConfig(
            Tool: tool,
            Image: FirstNonBlank(agent?.Image, unit.Image),
            Runtime: FirstNonBlank(agent?.Runtime, unit.Runtime),
            // Hosting mode is agent-owned. Default (Ephemeral) when the
            // agent has no execution block at all.
            Hosting: agent?.Hosting ?? AgentHostingMode.Ephemeral,
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
        // Preferred: top-level `execution: { tool, image, runtime, hosting, provider, model }`.
        if (definition.TryGetProperty("execution", out var exec) &&
            exec.ValueKind == JsonValueKind.Object)
        {
            var tool = GetStringOrNull(exec, "tool");
            var image = GetStringOrNull(exec, "image");
            var runtime = GetStringOrNull(exec, "runtime");
            var hosting = ParseHosting(GetStringOrNull(exec, "hosting"));
            var provider = GetStringOrNull(exec, "provider");
            var model = GetStringOrNull(exec, "model");

            if (tool is not null)
            {
                return new AgentExecutionConfig(tool, image, runtime, hosting, provider, model);
            }
        }

        // Legacy: `ai: { tool, environment: { image, runtime } }`.
        if (definition.TryGetProperty("ai", out var ai) && ai.ValueKind == JsonValueKind.Object)
        {
            var tool = GetStringOrNull(ai, "tool");
            if (ai.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                var image = GetStringOrNull(env, "image");
                var runtime = GetStringOrNull(env, "runtime");
                if (tool is not null && image is not null)
                {
                    return new AgentExecutionConfig(tool, image, runtime);
                }
            }
        }

        return null;
    }

    private static AgentHostingMode ParseHosting(string? value)
    {
        if (value is null)
        {
            return AgentHostingMode.Ephemeral;
        }

        if (value.Equals("persistent", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Persistent;
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

        return AgentHostingMode.Ephemeral;
    }

    private static string? GetStringOrNull(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}