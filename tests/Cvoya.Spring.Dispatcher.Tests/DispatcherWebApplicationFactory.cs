// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Configuration;

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

    /// <summary>
    /// Per-fixture workspace root the dispatcher is configured to use. Lives
    /// under <see cref="Path.GetTempPath"/> so the workspace materialiser can
    /// write through the real filesystem during integration tests without
    /// requiring the production default (<c>/var/lib/spring-workspaces</c>) to
    /// exist.
    /// </summary>
    public string WorkspaceRoot { get; } =
        Path.Combine(Path.GetTempPath(), "spring-dispatcher-tests-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(WorkspaceRoot);

        builder.UseSetting($"{DispatcherOptions.SectionName}:Tokens:{ValidToken}:TenantId", ValidTenantId);
        builder.UseSetting($"{DispatcherOptions.SectionName}:WorkspaceRoot", WorkspaceRoot);

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

            // The real ContainerRuntimeBinaryConfigurationRequirement probes
            // PATH for the configured binary and aborts host boot when it
            // isn't there. Test machines rarely have podman installed — swap
            // in a stubbed probe that always resolves so the dispatcher boots
            // for endpoint tests.
            var probeDescriptors = services
                .Where(d => d.ServiceType == typeof(IContainerRuntimeBinaryProbe))
                .ToList();
            foreach (var descriptor in probeDescriptors)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton<IContainerRuntimeBinaryProbe>(new StubContainerRuntimeBinaryProbe());
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(WorkspaceRoot))
                {
                    Directory.Delete(WorkspaceRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup — leaking a temp dir on test teardown is
                // not worth failing the build over.
            }
        }
    }

    private sealed class StubContainerRuntimeBinaryProbe : IContainerRuntimeBinaryProbe
    {
        public string? TryResolveBinary(string binaryName, System.Threading.CancellationToken cancellationToken) =>
            $"/stub/{binaryName}";
    }
}