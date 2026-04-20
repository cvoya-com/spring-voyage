// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.AgentRuntimes;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tenant seed provider that auto-installs every DI-registered
/// <see cref="IAgentRuntime"/> onto the bootstrapped tenant. The install
/// service is idempotent, so re-running this provider on an existing
/// tenant is a no-op against previously installed rows.
/// </summary>
/// <remarks>
/// Registered as a Singleton (the <see cref="ITenantSeedProvider"/> slot
/// on the bootstrap hosted service is root-scoped); opens a child DI
/// scope per call to resolve the scoped
/// <see cref="ITenantAgentRuntimeInstallService"/>. The ambient
/// <see cref="ITenantScopeBypass"/> opened by the bootstrap service
/// flows through the child scope via async-local state.
/// </remarks>
public sealed class AgentRuntimeInstallSeedProvider(
    IAgentRuntimeRegistry registry,
    IServiceScopeFactory scopeFactory,
    ILogger<AgentRuntimeInstallSeedProvider> logger) : ITenantSeedProvider
{
    /// <inheritdoc />
    public string Id => "agent-runtimes";

    /// <summary>
    /// Runs after skill bundles (priority 10) — agent runtimes reference
    /// no other seeded data, but grouping infrastructure at priorities
    /// 10–30 keeps the bootstrap log coherent.
    /// </summary>
    public int Priority => 20;

    /// <inheritdoc />
    public async Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var runtimes = registry.All;
        if (runtimes.Count == 0)
        {
            logger.LogInformation(
                "Tenant '{TenantId}' agent-runtime seed: no runtimes registered with the host; nothing to install.",
                tenantId);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var installService = scope.ServiceProvider
            .GetRequiredService<ITenantAgentRuntimeInstallService>();

        foreach (var runtime in runtimes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Tenant '{TenantId}' agent-runtime seed: seeding runtime '{RuntimeId}'.",
                tenantId, runtime.Id);
            await installService.InstallAsync(runtime.Id, config: null, cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "Tenant '{TenantId}' agent-runtime seed: processed {Count} runtime(s).",
            tenantId, runtimes.Count);
    }
}