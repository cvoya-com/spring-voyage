// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IOrchestrationStrategyProvider"/> — reads the
/// persisted <c>UnitDefinitions.Definition</c> JSON document and extracts
/// the <c>orchestration.strategy</c> key authored in the YAML manifest.
/// Used by <c>DefaultOrchestrationStrategyResolver</c> to pick the right
/// keyed <see cref="IOrchestrationStrategy"/> at dispatch time (#491).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the shape of <see cref="Capabilities.DbExpertiseSeedProvider"/>:
/// definition JSON is the single source of declarative truth written by
/// <c>UnitCreationService</c> at manifest ingestion time. A missing row or a
/// missing <c>orchestration</c> block surfaces as <c>null</c>, which the
/// resolver interprets as "fall back to policy inference / unkeyed default".
/// </para>
/// <para>
/// Read failures (e.g. a transient DB hiccup) also surface as "no key" —
/// the resolver logs and the unit's next message still dispatches through
/// the unkeyed default. Hard-failing here would block every message for a
/// unit whose manifest legitimately declared a strategy; degrading to the
/// default keeps traffic flowing while the misconfiguration surfaces
/// through the warning log.
/// </para>
/// </remarks>
public class DbOrchestrationStrategyProvider(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IOrchestrationStrategyProvider
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbOrchestrationStrategyProvider>();

    /// <inheritdoc />
    public async Task<string?> GetStrategyKeyAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            return null;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.UnitDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    u => (u.UnitId == unitId || u.ActorId == unitId) && u.DeletedAt == null,
                    cancellationToken);

            return entity is null ? null : ExtractStrategyKey(entity.Definition);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read orchestration.strategy for unit '{UnitId}'; treating as unset and falling back to the default strategy.",
                unitId);
            return null;
        }
    }

    /// <summary>
    /// Pulls an <c>orchestration.strategy</c> string out of a persisted
    /// definition JSON document. Returns <c>null</c> when the block is
    /// absent, not an object, has no <c>strategy</c> field, or the field is
    /// not a non-empty string — all treated as "no manifest directive" so
    /// the resolver can continue through its fallback ladder.
    /// </summary>
    internal static string? ExtractStrategyKey(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        if (!element.TryGetProperty("orchestration", out var orchestration)
            || orchestration.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!orchestration.TryGetProperty("strategy", out var strategy)
            || strategy.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var key = strategy.GetString();
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}