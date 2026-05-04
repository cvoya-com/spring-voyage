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
/// Default <see cref="IUnitExecutionStore"/> (#601 / #603 / #409). Reads
/// and writes the <c>execution</c> block on the persisted
/// <c>UnitDefinitions.Definition</c> JSON — the same document the agent
/// definition provider reads at dispatch time through the
/// <see cref="Cvoya.Spring.Core.Execution.IAgentDefinitionProvider"/>
/// merge path (see <see cref="DbAgentDefinitionProvider"/>).
/// </summary>
/// <remarks>
/// <para>
/// Lookup is by the unit's actor <c>Guid</c> (formatted with
/// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter"/>'s "N" form).
/// The <c>unitId</c> argument MUST be parseable as a Guid — passing a
/// user-facing display name throws <see cref="ArgumentException"/>.
/// Callers that hold only a name should resolve to the actor Guid through
/// the directory before calling in (the HTTP surface does this in its
/// route handlers; see #1666 for the regression that motivated the
/// clarification). Write semantics match
/// <c>DbUnitOrchestrationStore.SetStrategyKeyAsync</c>: the
/// <c>execution</c> slot is rewritten in place and every other property
/// on the Definition document (instructions / expertise / orchestration)
/// is preserved verbatim.
/// </para>
/// <para>
/// Partial updates are supported: a non-null field on
/// <see cref="UnitExecutionDefaults"/> replaces the corresponding slot;
/// a null field leaves the existing persisted value alone. An all-null
/// input (or an explicit <see cref="ClearAsync"/> call) strips the
/// entire block so the resolver falls through to "no unit default".
/// </para>
/// </remarks>
public class DbUnitExecutionStore(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IUnitExecutionStore
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbUnitExecutionStore>();

    /// <inheritdoc />
    public async Task<UnitExecutionDefaults?> GetAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId)
            || !Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var unitUuid))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Id == unitUuid && u.DeletedAt == null,
                cancellationToken);

        return entity is null ? null : Extract(entity.Definition);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string unitId,
        UnitExecutionDefaults defaults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            throw new ArgumentException("Unit id must be supplied.", nameof(unitId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var unitUuid))
        {
            throw new ArgumentException(
                $"Unit id '{unitId}' is not a valid Guid.", nameof(unitId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(
                u => u.Id == unitUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning(
                "Unit '{UnitId}': no UnitDefinition row found; execution defaults not persisted.",
                unitId);
            return;
        }

        // Partial-update semantics: merge the supplied non-null fields
        // with whatever is already on the persisted execution block.
        var existing = Extract(entity.Definition) ?? new UnitExecutionDefaults();
        var merged = new UnitExecutionDefaults(
            Image: PickTrimmed(defaults.Image, existing.Image),
            Runtime: PickTrimmed(defaults.Runtime, existing.Runtime),
            Tool: PickTrimmed(defaults.Tool, existing.Tool),
            Provider: PickTrimmed(defaults.Provider, existing.Provider),
            Model: PickTrimmed(defaults.Model, existing.Model));

        await PersistAsync(db, entity, merged, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            throw new ArgumentException("Unit id must be supplied.", nameof(unitId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var unitUuid))
        {
            throw new ArgumentException(
                $"Unit id '{unitId}' is not a valid Guid.", nameof(unitId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(
                u => u.Id == unitUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            return;
        }

        await PersistAsync(db, entity, new UnitExecutionDefaults(), cancellationToken);
    }

    private static async Task PersistAsync(
        SpringDbContext db,
        Data.Entities.UnitDefinitionEntity entity,
        UnitExecutionDefaults defaults,
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

        if (!defaults.IsEmpty)
        {
            var block = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(defaults.Image)) block["image"] = defaults.Image!.Trim();
            if (!string.IsNullOrWhiteSpace(defaults.Runtime)) block["runtime"] = defaults.Runtime!.Trim();
            if (!string.IsNullOrWhiteSpace(defaults.Tool)) block["tool"] = defaults.Tool!.Trim();
            if (!string.IsNullOrWhiteSpace(defaults.Provider)) block["provider"] = defaults.Provider!.Trim();
            if (!string.IsNullOrWhiteSpace(defaults.Model)) block["model"] = defaults.Model!.Trim();
            payload["execution"] = block;
        }

        entity.Definition = JsonSerializer.SerializeToElement(payload);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Extracts the <c>execution</c> block from a persisted definition
    /// document. Matches the tolerance contract on the agent definition
    /// provider's <c>ExtractExecution</c> — missing block, wrong JSON
    /// shape, empty strings all degrade to <c>null</c> so the resolver
    /// can continue through its fallback chain.
    /// </summary>
    internal static UnitExecutionDefaults? Extract(JsonElement? definition)
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

        var image = GetStringOrNull(exec, "image");
        var runtime = GetStringOrNull(exec, "runtime");
        var tool = GetStringOrNull(exec, "tool");
        var provider = GetStringOrNull(exec, "provider");
        var model = GetStringOrNull(exec, "model");

        var shaped = new UnitExecutionDefaults(image, runtime, tool, provider, model);
        return shaped.IsEmpty ? null : shaped;
    }

    private static string? PickTrimmed(string? next, string? current)
    {
        if (next is null)
        {
            // Null means "leave unchanged" — return the existing value.
            return current;
        }
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