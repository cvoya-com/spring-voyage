// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Background service that walks every registered <c>agent://</c> entity,
/// reads its legacy <c>StateKeys.AgentParentUnit</c> cached pointer via
/// <see cref="IAgentActor.GetMetadataAsync"/>, and upserts a corresponding
/// <see cref="UnitMembership"/> row so the M:N membership table reflects
/// the prior 1:N parent-unit world (see #160 / C2b-1). Idempotent: uses
/// <see cref="IUnitMembershipRepository.UpsertAsync"/>, so repeat runs
/// are harmless.
/// </summary>
/// <remarks>
/// <para>
/// Gated by <see cref="DatabaseOptions.BackfillMemberships"/> (default
/// <c>true</c>). Operators who have already run this migration can
/// disable it in configuration.
/// </para>
/// <para>
/// Runs as a <see cref="BackgroundService"/> so that failures (e.g., the
/// Dapr sidecar not being ready yet) never crash the host. Retries up to
/// 3 times with a 30-second backoff between attempts and an initial
/// 15-second delay to let the sidecar and placement service stabilize.
/// See #385.
/// </para>
/// </remarks>
public class UnitMembershipBackfillService(
    IServiceProvider services,
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory,
    IOptions<DatabaseOptions> options,
    ILogger<UnitMembershipBackfillService> logger) : BackgroundService
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    internal readonly DatabaseOptions _options = options.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BackfillMemberships)
        {
            logger.LogInformation(
                "Database:BackfillMemberships disabled — skipping unit-membership backfill.");
            return;
        }

        await Task.Delay(InitialDelay, stoppingToken);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await RunBackfillAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // Host is shutting down — propagate.
            }
            catch (Exception ex)
            {
                if (attempt < MaxAttempts)
                {
                    logger.LogWarning(ex,
                        "Membership backfill attempt {Attempt}/{MaxAttempts} failed; retrying in {RetrySeconds}s.",
                        attempt, MaxAttempts, (int)RetryDelay.TotalSeconds);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                else
                {
                    logger.LogWarning(ex,
                        "Membership backfill exhausted all {MaxAttempts} attempts. "
                        + "Skipping — data will be backfilled on next restart.",
                        MaxAttempts);
                }
            }
        }
    }

    internal async Task RunBackfillAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();

        var entries = await directoryService.ListAllAsync(cancellationToken);

        var agents = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (agents.Count == 0)
        {
            logger.LogDebug("Unit-membership backfill found no agent entries.");
            return;
        }

        var upserted = 0;
        foreach (var entry in agents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                    new ActorId(entry.ActorId), nameof(AgentActor));
                var metadata = await proxy.GetMetadataAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(metadata.ParentUnit))
                {
                    continue;
                }

                // Only upsert when no row already exists — we don't want to
                // overwrite per-membership overrides an operator may have
                // already written via the new endpoints.
                var existing = await repository.GetAsync(metadata.ParentUnit!, entry.Address.Path, cancellationToken);
                if (existing is not null)
                {
                    continue;
                }

                await repository.UpsertAsync(
                    new UnitMembership(
                        UnitId: metadata.ParentUnit!,
                        AgentAddress: entry.Address.Path,
                        Enabled: true),
                    cancellationToken);
                upserted++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Unit-membership backfill failed for agent {AgentId}; continuing.",
                    entry.Address.Path);
            }
        }

        logger.LogInformation(
            "Unit-membership backfill completed: {Upserted} row(s) upserted from {Scanned} agent entr{Plural}.",
            upserted, agents.Count, agents.Count == 1 ? "y" : "ies");
    }
}