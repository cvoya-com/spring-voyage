// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ContainerHealthMetricsService"/> lifecycle safety.
/// </summary>
public class ContainerHealthMetricsServiceTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly PersistentAgentRegistry _registry;

    public ContainerHealthMetricsServiceTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // Build the minimal DI graph needed to resolve PersistentAgentRegistry.
        var services = new ServiceCollection();
        services.AddSingleton(_containerRuntime);
        services.AddSingleton(Substitute.For<IHttpClientFactory>());
        services.AddSingleton(_loggerFactory);
        services.AddSingleton(Substitute.For<IDaprSidecarManager>());
        services.AddSingleton(Options.Create(new DaprSidecarOptions()));
        services.AddSingleton<ContainerLifecycleManager>();
        services.AddSingleton<AgentVolumeManager>();
        services.AddSingleton(Substitute.For<IAgentDefinitionProvider>());
        services.AddSingleton(Substitute.For<IMcpServer>());
        var launcher = Substitute.For<IAgentToolLauncher>();
        launcher.Tool.Returns("tool");
        services.AddSingleton(launcher);
        services.AddSingleton<IEnumerable<IAgentToolLauncher>>(_ => new[] { launcher });
        services.AddSingleton<PersistentAgentRegistry>();

        _registry = services.BuildServiceProvider().GetRequiredService<PersistentAgentRegistry>();
    }

    private ContainerHealthMetricsService Build() =>
        new(_registry, _containerRuntime, _loggerFactory);

    /// <summary>
    /// Regression guard for the NullReferenceException in
    /// <see cref="ContainerHealthMetricsService.StopAsync"/> that fires when the
    /// host shutdown path calls StopAsync on a service whose StartAsync was never
    /// invoked (e.g. when the host fails partway through the IHostedService
    /// startup sequence).
    /// </summary>
    [Fact]
    public async Task StopAsync_WithoutPriorStartAsync_DoesNotThrow()
    {
        using var svc = Build();

        // Must not throw even though StartAsync was never called and _timer is null.
        await Should.NotThrowAsync(
            () => svc.StopAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Validates that calling Dispose before StopAsync does not cause StopAsync
    /// to NRE on a non-null-but-already-disposed timer reference.  This
    /// matches the abnormal-shutdown ordering observed in WebApplicationFactory
    /// test teardown when the host startup throws after some IHostedServices
    /// have already been started.
    /// </summary>
    [Fact]
    public async Task StopAsync_AfterDispose_DoesNotThrow()
    {
        var svc = Build();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        // Dispose first (abnormal ordering that triggers the NRE in the original code).
        svc.Dispose();

        // StopAsync must tolerate the already-disposed timer.
        await Should.NotThrowAsync(
            () => svc.StopAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies the normal lifecycle: StartAsync followed by StopAsync completes
    /// without throwing.
    /// </summary>
    [Fact]
    public async Task StopAsync_AfterStartAsync_DoesNotThrow()
    {
        using var svc = Build();
        var ct = TestContext.Current.CancellationToken;

        await svc.StartAsync(ct);

        await Should.NotThrowAsync(() => svc.StopAsync(ct));
    }
}