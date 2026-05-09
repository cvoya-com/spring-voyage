// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Units;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitValidationTracker"/>. Writes the
/// <c>LastValidationRunId</c> and <c>LastValidationErrorJson</c> columns
/// on the <c>UnitDefinitionEntity</c> row matching the supplied actor id.
/// </summary>
/// <remarks>
/// Every call opens its own <see cref="IServiceScope"/> (matching the
/// pattern used by <c>DbUnitExecutionStore</c>) because <c>UnitActor</c> is instantiated
/// through the Dapr actor runtime, which does not own a request scope.
/// </remarks>
public class DbUnitValidationTracker(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IUnitValidationTracker
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbUnitValidationTracker>();

    /// <inheritdoc />
    public async Task<string?> GetLastValidationRunIdAsync(
        string unitActorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitActorId)
            || !Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitActorId, out var unitActorUuid))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.UnitDefinitions
            .AsNoTracking()
            .Where(u => u.Id == unitActorUuid && u.DeletedAt == null)
            .Select(u => new { u.LastValidationRunId })
            .FirstOrDefaultAsync(cancellationToken);

        return row?.LastValidationRunId;
    }

    /// <inheritdoc />
    public async Task BeginRunAsync(
        string unitActorId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitActorId))
        {
            throw new ArgumentException("Unit actor id must be supplied.", nameof(unitActorId));
        }
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id must be supplied.", nameof(runId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitActorId, out var unitActorUuid))
        {
            throw new ArgumentException(
                $"Unit actor id '{unitActorId}' is not a valid Guid.",
                nameof(unitActorId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(
                u => u.Id == unitActorUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning(
                "No UnitDefinition row for actor id {ActorId}; validation run id not persisted.",
                unitActorId);
            return;
        }

        entity.LastValidationRunId = runId;
        // Clear any stale failure blob atomically with the run id write so
        // an observer never sees "new run id + old error." The failure
        // payload for this new run, if any, lands later via SetFailureAsync.
        entity.LastValidationErrorJson = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetFailureAsync(
        string unitActorId,
        string? errorJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitActorId))
        {
            throw new ArgumentException("Unit actor id must be supplied.", nameof(unitActorId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitActorId, out var unitActorUuid))
        {
            throw new ArgumentException(
                $"Unit actor id '{unitActorId}' is not a valid Guid.",
                nameof(unitActorId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(
                u => u.Id == unitActorUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning(
                "No UnitDefinition row for actor id {ActorId}; validation failure not persisted.",
                unitActorId);
            return;
        }

        entity.LastValidationErrorJson = errorJson;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }
}
