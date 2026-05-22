// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IAgentExecutionStore"/> (#601 / #603 / #409 B-wide).
/// Reads and writes the <c>execution</c> block on the persisted
/// <c>AgentDefinitions.Definition</c> JSON. Partial-update semantics
/// match <see cref="DbUnitExecutionStore"/>: a null field on
/// <see cref="AgentExecutionShape"/> leaves the existing slot alone.
/// </summary>
public class DbAgentExecutionStore(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IAgentExecutionStore
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbAgentExecutionStore>();

    /// <inheritdoc />
    public async Task<AgentExecutionShape?> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)
            || !Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(agentId, out var agentUuid))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.AgentDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Id == agentUuid && a.DeletedAt == null,
                cancellationToken);

        return entity is null ? null : Extract(entity.Definition);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string agentId,
        AgentExecutionShape shape,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id must be supplied.", nameof(agentId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(agentId, out var agentUuid))
        {
            throw new ArgumentException(
                $"Agent id '{agentId}' is not a valid Guid.", nameof(agentId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.AgentDefinitions
            .FirstOrDefaultAsync(
                a => a.Id == agentUuid && a.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning(
                "Agent '{AgentId}': no AgentDefinition row found; execution block not persisted.",
                agentId);
            return;
        }

        var existing = Extract(entity.Definition) ?? new AgentExecutionShape();
        var merged = new AgentExecutionShape(
            Image: PickTrimmed(shape.Image, existing.Image),
            Model: shape.Model ?? existing.Model,
            Hosting: PickTrimmed(shape.Hosting, existing.Hosting),
            Runtime: PickTrimmed(shape.Runtime, existing.Runtime));

        await PersistAsync(db, entity, merged, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id must be supplied.", nameof(agentId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(agentId, out var agentUuid))
        {
            throw new ArgumentException(
                $"Agent id '{agentId}' is not a valid Guid.", nameof(agentId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.AgentDefinitions
            .FirstOrDefaultAsync(
                a => a.Id == agentUuid && a.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            return;
        }

        await PersistAsync(db, entity, new AgentExecutionShape(), cancellationToken);
    }

    private static async Task PersistAsync(
        SpringDbContext db,
        Data.Entities.AgentDefinitionEntity entity,
        AgentExecutionShape shape,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>();

        if (entity.Definition is { ValueKind: JsonValueKind.Object } existing)
        {
            foreach (var prop in existing.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "execution", StringComparison.OrdinalIgnoreCase))
                {
                    payload[prop.Name] = prop.Value;
                }
            }
        }

        if (!shape.IsEmpty)
        {
            var block = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(shape.Image)) block["image"] = shape.Image!.Trim();
            if (!string.IsNullOrWhiteSpace(shape.Runtime)) block["runtime"] = shape.Runtime!.Trim();
            if (!string.IsNullOrWhiteSpace(shape.Hosting)) block["hosting"] = shape.Hosting!.Trim();
            ExecutionJson.WriteModel(block, shape.Model);
            payload["execution"] = block;
        }

        entity.Definition = JsonSerializer.SerializeToElement(payload);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static AgentExecutionShape? Extract(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        if (!element.TryGetProperty("execution", out var exec) ||
            exec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var shape = new AgentExecutionShape(
            Image: GetStringOrNull(exec, "image"),
            Model: ExecutionJson.ReadModel(exec),
            Hosting: GetStringOrNull(exec, "hosting"),
            Runtime: GetStringOrNull(exec, "runtime"));

        return shape.IsEmpty ? null : shape;
    }

    private static string? PickTrimmed(string? next, string? current)
    {
        if (next is null) return current;
        var trimmed = next.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? GetStringOrNull(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
