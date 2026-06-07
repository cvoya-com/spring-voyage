// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitExecutionStore"/> (#601 / #603 / #409). Reads
/// and writes the <c>image</c> / <c>runtime</c> / <c>system_prompt_mode</c>
/// slots on the persisted <c>UnitDefinitions.Definition</c> JSON — the same
/// document the agent definition provider reads at dispatch time through the
/// <see cref="Cvoya.Spring.Core.Execution.IAgentDefinitionProvider"/>
/// merge path (see <see cref="DbAgentDefinitionProvider"/>).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0067 §2 (#3111): the unit's <c>model</c> has a single writable home —
/// <c>unit_live_config.{provider,model}</c> — not the definition jsonb. This
/// store therefore reads and writes <see cref="UnitExecutionDefaults.Model"/>
/// against the <c>unit_live_config</c> row (mapping
/// <see cref="Model.Provider"/> ↔ <c>provider</c> and <see cref="Model.Id"/>
/// ↔ <c>model</c>), while <c>image</c> / <c>runtime</c> /
/// <c>system_prompt_mode</c> stay on the jsonb. The
/// <see cref="UnitExecutionDefaults"/> shape is unchanged, so the member-agent
/// inheritance default (<see cref="DbAgentDefinitionProvider.Merge"/>) and the
/// cross-parent intersection (<c>ExecutionConfigInheritanceResolver</c>)
/// transparently consume the single-home model. Unit <c>hosting</c> already
/// lives only on <c>unit_live_config</c>; it is not part of this shape and is
/// never written to the jsonb here.
/// </para>
/// <para>
/// Lookup is by the unit's actor <c>Guid</c> (formatted with
/// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter"/>'s "N" form).
/// The <c>unitId</c> argument MUST be parseable as a Guid — passing a
/// user-facing display name throws <see cref="ArgumentException"/>.
/// Callers that hold only a name should resolve to the actor Guid through
/// the directory before calling in (the HTTP surface does this in its
/// route handlers; see #1666 for the regression that motivated the
/// clarification). The jsonb slots are rewritten in place and every other
/// property on the Definition document (instructions / expertise) is
/// preserved verbatim.
/// </para>
/// <para>
/// Partial updates are supported: a non-null field on
/// <see cref="UnitExecutionDefaults"/> replaces the corresponding slot;
/// a null field leaves the existing persisted value alone. An all-null
/// input (or an explicit <see cref="ClearAsync"/> call) strips the jsonb
/// block and clears <c>unit_live_config.{provider,model}</c> so the
/// resolver falls through to "no unit default".
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

        if (entity is null)
        {
            return null;
        }

        // ADR-0067 §2: the model home is unit_live_config; everything else on
        // the execution shape comes from the jsonb. Combine the two so callers
        // (Merge / inheritance resolver / dispatch projection) see one shape.
        var liveConfig = await db.UnitLiveConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UnitId == unitUuid, cancellationToken);

        var jsonbSlots = ExtractJsonbSlots(entity.Definition);
        var model = ReadLiveConfigModel(liveConfig);

        var shaped = new UnitExecutionDefaults(
            Image: jsonbSlots.Image,
            Model: model,
            Runtime: jsonbSlots.Runtime,
            SystemPromptMode: jsonbSlots.SystemPromptMode);

        return shaped.IsEmpty ? null : shaped;
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

        // Partial-update semantics for the jsonb slots (image / runtime /
        // system_prompt_mode): merge the supplied non-null fields with whatever
        // is already on the persisted execution block.
        var existing = ExtractJsonbSlots(entity.Definition);
        var mergedImage = PickTrimmed(defaults.Image, existing.Image);
        var mergedRuntime = PickTrimmed(defaults.Runtime, existing.Runtime);
        var mergedSystemPromptMode = defaults.SystemPromptMode ?? existing.SystemPromptMode;

        PersistJsonb(entity, mergedImage, mergedRuntime, mergedSystemPromptMode);

        // ADR-0067 §2: the model home is unit_live_config. A non-null model
        // replaces the (provider, id) pair there; a null model leaves the
        // existing live-config value alone (partial update).
        if (defaults.Model is not null)
        {
            var row = await db.UnitLiveConfigs
                .FirstOrDefaultAsync(c => c.UnitId == unitUuid, cancellationToken);
            if (row is null)
            {
                row = new UnitLiveConfigEntity { UnitId = unitUuid };
                db.UnitLiveConfigs.Add(row);
            }
            row.Provider = defaults.Model.Provider;
            row.Model = defaults.Model.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
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

        // Strip the jsonb execution block entirely.
        PersistJsonb(entity, image: null, runtime: null, systemPromptMode: null);

        // ADR-0067 §2: clear the model home on unit_live_config too so the
        // "no unit default" state is consistent across both stores. Leaves the
        // other live-config dimensions (hosting / color / specialty / …) alone.
        var row = await db.UnitLiveConfigs
            .FirstOrDefaultAsync(c => c.UnitId == unitUuid, cancellationToken);
        if (row is not null)
        {
            row.Provider = null;
            row.Model = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void PersistJsonb(
        Data.Entities.UnitDefinitionEntity entity,
        string? image,
        string? runtime,
        Cvoya.Spring.Core.Catalog.SystemPromptMode? systemPromptMode)
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

        var hasBlock = !string.IsNullOrWhiteSpace(image)
            || !string.IsNullOrWhiteSpace(runtime)
            || systemPromptMode is not null;

        if (hasBlock)
        {
            var block = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(image)) block["image"] = image!.Trim();
            if (!string.IsNullOrWhiteSpace(runtime)) block["runtime"] = runtime!.Trim();
            // ADR-0067 §2: model and hosting are no longer written to the unit
            // jsonb — model lives on unit_live_config, hosting always did.
            ExecutionJson.WriteSystemPromptMode(block, systemPromptMode);
            payload["execution"] = block;
        }

        entity.Definition = JsonSerializer.SerializeToElement(payload);
    }

    private static Model? ReadLiveConfigModel(UnitLiveConfigEntity? liveConfig)
    {
        if (liveConfig is null
            || string.IsNullOrWhiteSpace(liveConfig.Provider)
            || string.IsNullOrWhiteSpace(liveConfig.Model))
        {
            return null;
        }
        return new Model(liveConfig.Provider!.Trim(), liveConfig.Model!.Trim());
    }

    /// <summary>
    /// Extracts the jsonb-homed execution slots (image / runtime /
    /// system_prompt_mode) from a persisted definition document. Matches the
    /// tolerance contract on the agent definition provider's
    /// <c>ExtractExecution</c> — missing block, wrong JSON shape, empty
    /// strings all degrade to <c>null</c>. ADR-0067 §2: <c>model</c> and
    /// <c>hosting</c> are deliberately not read from the jsonb — the unit's
    /// model home is <c>unit_live_config</c> and hosting always lived there.
    /// </summary>
    internal static JsonbSlots ExtractJsonbSlots(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("execution", out var exec)
            || exec.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return new JsonbSlots(
            Image: GetStringOrNull(exec, "image"),
            Runtime: GetStringOrNull(exec, "runtime"),
            SystemPromptMode: ExecutionJson.ReadSystemPromptMode(exec));
    }

    /// <summary>
    /// The jsonb-homed slots of a unit's execution block (ADR-0067 §2):
    /// everything except <c>model</c> (lives on <c>unit_live_config</c>) and
    /// <c>hosting</c> (already lived on <c>unit_live_config</c>).
    /// </summary>
    internal readonly record struct JsonbSlots(
        string? Image,
        string? Runtime,
        Cvoya.Spring.Core.Catalog.SystemPromptMode? SystemPromptMode);

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
