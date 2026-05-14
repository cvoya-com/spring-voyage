// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.RuntimeCatalog.DependencyInjection;

using Cvoya.Spring.Core.Catalog;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extension methods for the runtime catalogue (ADR-0038).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IRuntimeCatalog"/> singleton loaded from the
    /// embedded <c>eng/runtime-catalog/runtime-catalog.yaml</c> resource. Idempotent
    /// via <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection, Func{IServiceProvider, TService})"/>
    /// — a host that registers a custom catalogue (for instance the cloud
    /// host's tenant-scoped variant) before calling this extension keeps
    /// its registration.
    /// </summary>
    public static IServiceCollection AddCvoyaSpringRuntimeCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRuntimeCatalog>(_ => RuntimeCatalogLoader.LoadEmbedded());
        return services;
    }
}
