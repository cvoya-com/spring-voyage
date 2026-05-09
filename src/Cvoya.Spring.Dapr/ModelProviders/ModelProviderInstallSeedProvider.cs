// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.ModelProviders;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tenant seed provider that auto-installs every model provider declared
/// in <c>platform/runtime-catalog.yaml</c> onto the bootstrapped tenant.
/// The install service is idempotent, so re-running this provider on an
/// existing tenant is a no-op against previously installed rows.
/// </summary>
/// <remarks>
/// Per ADR-0038 § "Provider is the credential and routing boundary",
/// installs are keyed on provider id (not runtime id) so a single
/// Anthropic credential is shared across every runtime that consumes it.
/// </remarks>
public sealed class ModelProviderInstallSeedProvider(
    IRuntimeCatalog catalog,
    IServiceScopeFactory scopeFactory,
    ILogger<ModelProviderInstallSeedProvider> logger) : ITenantSeedProvider
{
    /// <inheritdoc />
    public string Id => "model-providers";

    /// <inheritdoc />
    public int Priority => 20;

    /// <inheritdoc />
    public async Task ApplySeedsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must be supplied.", nameof(tenantId));
        }

        var providers = catalog.ModelProviders;
        if (providers.Count == 0)
        {
            logger.LogInformation(
                "Tenant '{TenantId}' model-provider seed: no providers declared in the runtime catalogue; nothing to install.",
                tenantId);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var installService = scope.ServiceProvider
            .GetRequiredService<ITenantModelProviderInstallService>();

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Tenant '{TenantId}' model-provider seed: seeding provider '{ProviderId}'.",
                tenantId, provider.Id);
            var seedConfig = new ModelProviderInstallConfig(
                Models: provider.DefaultModels.ToArray(),
                DefaultModel: provider.DefaultModels.Count > 0 ? provider.DefaultModels[0] : null,
                BaseUrl: null);
            await installService.InstallAsync(provider.Id, seedConfig, cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "Tenant '{TenantId}' model-provider seed: processed {Count} provider(s).",
            tenantId, providers.Count);
    }
}
