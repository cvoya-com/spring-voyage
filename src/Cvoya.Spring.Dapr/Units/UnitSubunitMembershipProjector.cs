// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of
/// <see cref="IUnitSubunitMembershipProjector"/>. Creates a fresh
/// <c>IServiceScope</c> per call so the underlying scoped
/// <see cref="IUnitSubunitMembershipRepository"/> (and the
/// <c>SpringDbContext</c> it depends on) resolve cleanly from the
/// actor's singleton-style activation. Mirrors the
/// scope-per-write pattern used by
/// <c>Cvoya.Spring.Dapr.Routing.DirectoryService</c>.
/// </summary>
public class UnitSubunitMembershipProjector(
    IServiceScopeFactory scopeFactory,
    ILogger<UnitSubunitMembershipProjector> logger) : IUnitSubunitMembershipProjector
{
    /// <inheritdoc />
    public async Task ProjectAddAsync(string parentUnitId, string childUnitId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
            await repo.UpsertAsync(parentUnitId, childUnitId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Projection failures must never crash an actor turn — the
            // actor's own state write is authoritative. The startup
            // reconciliation service will replay this edge on the next
            // host boot.
            logger.LogWarning(ex,
                "Failed to project sub-unit membership add ({Parent} → {Child}); actor state remains the source of truth.",
                parentUnitId, childUnitId);
        }
    }

    /// <inheritdoc />
    public async Task ProjectRemoveAsync(string parentUnitId, string childUnitId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
            await repo.DeleteAsync(parentUnitId, childUnitId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to project sub-unit membership remove ({Parent} → {Child}); actor state remains the source of truth.",
                parentUnitId, childUnitId);
        }
    }
}