// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using Cvoya.Spring.Core.Execution;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

/// <summary>
/// Test harness for the dispatcher host. Replaces <see cref="IContainerRuntime"/>
/// with an NSubstitute double so endpoint tests can assert the exact
/// <c>ContainerConfig</c> forwarded to the runtime without shelling out to
/// <c>podman</c>.
/// </summary>
public class DispatcherWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The mock <see cref="IContainerRuntime"/> the dispatcher calls from its
    /// HTTP endpoints. Tests arrange return values on it and then assert the
    /// arguments it was invoked with.
    /// </summary>
    public IContainerRuntime ContainerRuntime { get; } = Substitute.For<IContainerRuntime>();

    /// <summary>Default bearer token pre-seeded on the host for authenticated tests.</summary>
    public const string ValidToken = "test-token-worker-1";

    /// <summary>Tenant id the <see cref="ValidToken"/> is scoped to.</summary>
    public const string ValidTenantId = "tenant-test";

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"{DispatcherOptions.SectionName}:Tokens:{ValidToken}:TenantId", ValidTenantId);

        builder.ConfigureServices(services =>
        {
            var existing = services
                .Where(d => d.ServiceType == typeof(IContainerRuntime))
                .ToList();
            foreach (var descriptor in existing)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(ContainerRuntime);
        });
    }
}