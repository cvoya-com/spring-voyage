// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.ModelProviders.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extension methods for the model-provider adapters (ADR-0038 decision 3).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the v0.1 closed set of <see cref="IModelProviderAdapter"/>
    /// strategies (Anthropic, OpenAI-compatible, Google) plus a default
    /// <see cref="IModelProviderAdapterRegistry"/>. Each adapter gets its
    /// own named <see cref="HttpClient"/> so the credential-health
    /// watchdog (CONVENTIONS.md § 16) can be attached at the host
    /// composition point.
    /// </summary>
    public static IServiceCollection AddCvoyaSpringModelProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(AnthropicAdapter.HttpClientName);
        services.AddHttpClient(OpenAiCompatibleAdapter.HttpClientName);
        services.AddHttpClient(GoogleAdapter.HttpClientName);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderAdapter, AnthropicAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderAdapter, OpenAiCompatibleAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModelProviderAdapter, GoogleAdapter>());

        services.TryAddSingleton<IModelProviderAdapterRegistry, ModelProviderAdapterRegistry>();

        return services;
    }
}

/// <summary>
/// Default DI-driven implementation of
/// <see cref="IModelProviderAdapterRegistry"/>.
/// </summary>
internal sealed class ModelProviderAdapterRegistry : IModelProviderAdapterRegistry
{
    private readonly Dictionary<string, IModelProviderAdapter> _byId;

    public ModelProviderAdapterRegistry(IEnumerable<IModelProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var list = adapters.ToList();
        All = list;
        _byId = list.ToDictionary(a => a.AdapterId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IModelProviderAdapter> All { get; }

    public IModelProviderAdapter? Get(string adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return null;
        }
        return _byId.GetValueOrDefault(adapterId);
    }
}
