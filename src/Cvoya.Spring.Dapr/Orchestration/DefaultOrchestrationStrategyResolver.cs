// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IOrchestrationStrategyResolver"/> — reads the
/// manifest-declared key via <see cref="IOrchestrationStrategyProvider"/>,
/// falls back to <c>label-routed</c> when the unit has a
/// <see cref="UnitPolicy.LabelRouting"/> slot (ADR-0007 revisit criterion),
/// and finally to the unkeyed platform default. Each message gets a fresh
/// DI scope so scoped strategies like
/// <see cref="LabelRoutedOrchestrationStrategy"/> can pick up hot
/// <see cref="IUnitPolicyRepository"/> edits without actor recycling.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is defensive: an unknown key on the manifest (declared but
/// not registered in DI) drops to the policy inference and then the
/// unkeyed default, logging a warning so operators can fix the manifest
/// without every message to the unit failing in the meantime. A bare-
/// configured unit (no manifest directive, no policy) resolves the unkeyed
/// default — the historical behaviour before #491.
/// </para>
/// </remarks>
public class DefaultOrchestrationStrategyResolver(
    IOrchestrationStrategyProvider strategyProvider,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IOrchestrationStrategyResolver
{
    /// <summary>
    /// DI key for the inferred label-routing fallback. Public-const so
    /// tests and hosts wiring additional strategies can refer to the same
    /// literal instead of a stringly-typed constant scattered in the code.
    /// </summary>
    public const string LabelRoutedKey = "label-routed";

    private readonly ILogger _logger = loggerFactory.CreateLogger<DefaultOrchestrationStrategyResolver>();

    /// <inheritdoc />
    public async Task<OrchestrationStrategyLease> ResolveAsync(
        string unitId,
        CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            // 1. Manifest-declared key wins when a registration matches.
            var manifestKey = await strategyProvider.GetStrategyKeyAsync(unitId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(manifestKey))
            {
                var keyed = scope.ServiceProvider.GetKeyedService<IOrchestrationStrategy>(manifestKey);
                if (keyed is not null)
                {
                    _logger.LogDebug(
                        "Unit {UnitId} orchestrated via manifest-declared strategy key '{Key}'.",
                        unitId, manifestKey);
                    return new OrchestrationStrategyLease(keyed, manifestKey, scope);
                }

                _logger.LogWarning(
                    "Unit {UnitId} declared orchestration.strategy='{Key}' but no IOrchestrationStrategy is registered under that key; falling back to policy inference / default.",
                    unitId, manifestKey);
            }

            // 2. Policy inference: LabelRouting slot implies label-routed.
            //
            // ADR-0007 revisit criterion: "When the manifest-driven strategy
            // selector lands (#491), LabelRouting should imply strategy:
            // label-routed by default so operators don't have to set both."
            if (await HasLabelRoutingAsync(scope, unitId, cancellationToken))
            {
                var labelRouted = scope.ServiceProvider.GetKeyedService<IOrchestrationStrategy>(LabelRoutedKey);
                if (labelRouted is not null)
                {
                    _logger.LogDebug(
                        "Unit {UnitId} has LabelRouting policy but no manifest strategy; resolving to '{Key}'.",
                        unitId, LabelRoutedKey);
                    return new OrchestrationStrategyLease(labelRouted, LabelRoutedKey, scope);
                }

                _logger.LogWarning(
                    "Unit {UnitId} has LabelRouting policy but '{Key}' strategy is not registered; falling back to unkeyed default.",
                    unitId, LabelRoutedKey);
            }

            // 3. Unkeyed default — the pre-#491 behaviour.
            var unkeyed = scope.ServiceProvider.GetRequiredService<IOrchestrationStrategy>();
            return new OrchestrationStrategyLease(unkeyed, null, scope);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Checks the unit's policy for a non-null <see cref="UnitPolicy.LabelRouting"/>.
    /// A missing repository registration, missing policy row, or read failure
    /// all surface as "no LabelRouting" — the resolver moves on to the
    /// unkeyed default. We don't want a transient policy-store outage to
    /// block orchestration when the manifest already declared the right
    /// strategy path either.
    /// </summary>
    private async Task<bool> HasLabelRoutingAsync(
        AsyncServiceScope scope,
        string unitId,
        CancellationToken cancellationToken)
    {
        var repo = scope.ServiceProvider.GetService<IUnitPolicyRepository>();
        if (repo is null)
        {
            return false;
        }

        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var unitGuid))
        {
            // Pre-#1629 callers (or test harnesses) sometimes pass a slug
            // instead of a Guid; the post-#1629 repository is Guid-keyed,
            // so we treat an unparseable id as "no LabelRouting".
            return false;
        }

        try
        {
            var policy = await repo.GetAsync(unitGuid, cancellationToken);
            return policy.LabelRouting is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit {UnitId}: failed to read UnitPolicy for LabelRouting inference; treating as no LabelRouting.",
                unitId);
            return false;
        }
    }
}