// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reads agent definitions from <see cref="SpringDbContext.AgentDefinitions"/>
/// and projects them into the <see cref="AgentDefinition"/> shape consumed by
/// the execution layer. Extracts the execution config from the persisted JSON
/// definition, tolerating two layouts: a top-level <c>execution</c> block or
/// the legacy <c>ai.environment</c> block produced by earlier CLI versions.
/// </summary>
public class DbAgentDefinitionProvider(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IAgentDefinitionProvider
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

        return Project(entity);
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

        return value.Equals("persistent", StringComparison.OrdinalIgnoreCase)
            ? AgentHostingMode.Persistent
            : AgentHostingMode.Ephemeral;
    }

    private static string? GetStringOrNull(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}