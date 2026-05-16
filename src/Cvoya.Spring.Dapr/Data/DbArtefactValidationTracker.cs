// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IArtefactValidationTracker"/>. For
/// <see cref="ArtefactKind.Unit"/>, writes the <c>LastValidationRunId</c> and
/// <c>LastValidationErrorJson</c> columns on the <c>UnitDefinitionEntity</c>
/// row matching the supplied actor id. For <see cref="ArtefactKind.Agent"/>,
/// all methods no-op — the agent_definitions table does not yet carry the
/// per-run tracking columns, so the agent path runs without the stale-run
/// guard for v0.1. Filed as a follow-up to add the columns + migration.
/// </summary>
/// <remarks>
/// Every unit-side call opens its own <see cref="IServiceScope"/> (matching
/// the pattern used by <c>DbUnitExecutionStore</c>) because the actors are
/// instantiated through the Dapr actor runtime, which does not own a
/// request scope.
/// </remarks>
public class DbArtefactValidationTracker(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IArtefactValidationTracker
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DbArtefactValidationTracker>();

    /// <inheritdoc />
    public async Task<string?> GetLastValidationRunIdAsync(
        ArtefactKind kind,
        string artefactActorId,
        CancellationToken cancellationToken = default)
    {
        if (kind != ArtefactKind.Unit)
        {
            // Agent / Skill / Workflow have no tracking row today. Return
            // null so the coordinator's stale-run guard treats every
            // completion as fresh — acceptable v0.1 limitation.
            return null;
        }

        if (string.IsNullOrWhiteSpace(artefactActorId)
            || !Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(artefactActorId, out var artefactActorUuid))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.UnitDefinitions
            .AsNoTracking()
            .Where(u => u.Id == artefactActorUuid && u.DeletedAt == null)
            .Select(u => new { u.LastValidationRunId })
            .FirstOrDefaultAsync(cancellationToken);

        return row?.LastValidationRunId;
    }

    /// <inheritdoc />
    public async Task BeginRunAsync(
        ArtefactKind kind,
        string artefactActorId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (kind != ArtefactKind.Unit)
        {
            // Agent path: no tracking row yet. No-op.
            return;
        }

        if (string.IsNullOrWhiteSpace(artefactActorId))
        {
            throw new ArgumentException("Artefact actor id must be supplied.", nameof(artefactActorId));
        }
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id must be supplied.", nameof(runId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(artefactActorId, out var artefactActorUuid))
        {
            throw new ArgumentException(
                $"Artefact actor id '{artefactActorId}' is not a valid Guid.",
                nameof(artefactActorId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(
                u => u.Id == artefactActorUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning(
                "No UnitDefinition row for actor id {ActorId}; validation run id not persisted.",
                artefactActorId);
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
        ArtefactKind kind,
        string artefactActorId,
        string? errorJson,
        CancellationToken cancellationToken = default)
    {
        if (kind != ArtefactKind.Unit)
        {
            // Agent path: no tracking row yet. No-op.
            return;
        }

        if (string.IsNullOrWhiteSpace(artefactActorId))
        {
            throw new ArgumentException("Artefact actor id must be supplied.", nameof(artefactActorId));
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(artefactActorId, out var artefactActorUuid))
        {
            throw new ArgumentException(
                $"Artefact actor id '{artefactActorId}' is not a valid Guid.",
                nameof(artefactActorId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var entity = await db.UnitDefinitions
            .FirstOrDefaultAsync(
                u => u.Id == artefactActorUuid && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning(
                "No UnitDefinition row for actor id {ActorId}; validation failure not persisted.",
                artefactActorId);
            return;
        }

        entity.LastValidationErrorJson = errorJson;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }
}
