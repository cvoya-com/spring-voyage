// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using System.Diagnostics;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitLiveConfigStore"/>.
/// Creates a fresh <c>IServiceScope</c> per call so the scoped
/// <see cref="IUnitLiveConfigRepository"/> (and its
/// <c>SpringDbContext</c>) resolves cleanly from the unit actor's
/// singleton-style activation. Logs the wall-clock duration of every
/// activation-path read (<see cref="GetMetadataAsync"/>,
/// <see cref="GetBoundaryAsync"/>,
/// <see cref="GetPermissionInheritanceAsync"/>,
/// <see cref="GetOwnExpertiseAsync"/>,
/// <see cref="HasOwnExpertiseSetAsync"/>) so the v0.2 cache decision
/// is data-driven (ADR-0040 § 3).
/// </summary>
public class UnitLiveConfigStore(
    IServiceScopeFactory scopeFactory,
    ILogger<UnitLiveConfigStore> logger) : IUnitLiveConfigStore
{
    /// <inheritdoc />
    public async Task<UnitMetadata> GetMetadataAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        var result = await repo.GetMetadataAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitLiveConfig.GetMetadata unit={UnitId} elapsedMs={ElapsedMs}",
            unitId, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid unitId, UnitMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        var written = await repo.UpsertMetadataAsync(unitId, metadata, cancellationToken);
        if (written.Count > 0)
        {
            logger.LogInformation(
                "Unit {UnitId} live-config updated: {Fields}",
                unitId, string.Join(",", written));
        }
        return written;
    }

    /// <inheritdoc />
    public async Task<UnitBoundary> GetBoundaryAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        var result = await repo.GetBoundaryAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitLiveConfig.GetBoundary unit={UnitId} isEmpty={IsEmpty} elapsedMs={ElapsedMs}",
            unitId, result.IsEmpty, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetBoundaryAsync(
        Guid unitId, UnitBoundary boundary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        await repo.SetBoundaryAsync(unitId, boundary, cancellationToken);
        logger.LogInformation(
            "Unit {UnitId} boundary updated. Opacities={Opacities} Projections={Projections} Syntheses={Syntheses}",
            unitId,
            boundary.Opacities?.Count ?? 0,
            boundary.Projections?.Count ?? 0,
            boundary.Syntheses?.Count ?? 0);
    }

    /// <inheritdoc />
    public async Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        var result = await repo.GetPermissionInheritanceAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitLiveConfig.GetPermissionInheritance unit={UnitId} value={Value} elapsedMs={ElapsedMs}",
            unitId, result, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetPermissionInheritanceAsync(
        Guid unitId,
        UnitPermissionInheritance inheritance,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        await repo.SetPermissionInheritanceAsync(unitId, inheritance, cancellationToken);
        logger.LogInformation(
            "Unit {UnitId} permission inheritance set to {Inheritance}",
            unitId, inheritance);
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetOwnExpertiseAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        var result = await repo.GetOwnExpertiseAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitLiveConfig.GetOwnExpertise unit={UnitId} count={Count} elapsedMs={ElapsedMs}",
            unitId, result.Length, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> SetOwnExpertiseAsync(
        Guid unitId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        var persisted = await repo.SetOwnExpertiseAsync(unitId, domains, cancellationToken);
        logger.LogInformation(
            "Unit {UnitId} own expertise replaced. Count: {Count}", unitId, persisted.Length);
        return persisted;
    }

    /// <inheritdoc />
    public async Task<bool> HasOwnExpertiseSetAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitLiveConfigRepository>();
        var result = await repo.HasOwnExpertiseSetAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitLiveConfig.HasOwnExpertiseSet unit={UnitId} value={Value} elapsedMs={ElapsedMs}",
            unitId, result, sw.Elapsed.TotalMilliseconds);
        return result;
    }
}
