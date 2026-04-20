// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google.DependencyInjection;

using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Extension methods that register the Google AI agent runtime with the DI
/// container. The registration is the only seam the host needs to wire — the
/// <see cref="IAgentRuntimeRegistry"/> picks the new <see cref="IAgentRuntime"/>
/// up automatically.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GoogleAgentRuntime"/> as an <see cref="IAgentRuntime"/>
    /// and configures the named <see cref="HttpClient"/> the runtime resolves
    /// at credential-validation time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime is added via <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
    /// so a downstream host (e.g. the private cloud repo) can register a
    /// replacement <see cref="GoogleAgentRuntime"/> before this extension
    /// runs and not be silently shadowed by the default. Re-invoking this
    /// extension is also safe — the second registration is dropped.
    /// </para>
    /// <para>
    /// <see cref="IServiceCollection.AddHttpClient(string)"/> is idempotent
    /// for a given client name, so callers may also register their own
    /// <see cref="HttpClient"/> handlers (proxies, retry policies) under
    /// <see cref="GoogleAgentRuntime.HttpClientName"/> before or after this
    /// call without losing them.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringAgentRuntimeGoogle(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(GoogleAgentRuntime.HttpClientName);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentRuntime, GoogleAgentRuntime>());

        return services;
    }
}