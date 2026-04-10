// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

/// <summary>
/// Unit tests for <see cref="ContainerLifecycleManager"/>.
/// </summary>
public class ContainerLifecycleManagerTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IDaprSidecarManager _sidecarManager = Substitute.For<IDaprSidecarManager>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ContainerLifecycleManager _manager;
    private readonly ContainerRuntimeOptions _options;

    public ContainerLifecycleManagerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _options = new ContainerRuntimeOptions { RuntimeType = "docker" };
        _manager = new ContainerLifecycleManager(
            _containerRuntime,
            _sidecarManager,
            Options.Create(_options),
            _loggerFactory);
    }

    [Fact]
    public async Task TeardownAsync_StopsContainerAndSidecar()
    {
        await _manager.TeardownAsync("app-container", "sidecar-container", "test-network", TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StopAsync("app-container", Arg.Any<CancellationToken>());
        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-container", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_SkipsNullContainerId()
    {
        await _manager.TeardownAsync(null, "sidecar-container", "test-network", TestContext.Current.CancellationToken);

        await _containerRuntime.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-container", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_SkipsNullSidecarId()
    {
        await _manager.TeardownAsync("app-container", null, "test-network", TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StopAsync("app-container", Arg.Any<CancellationToken>());
        await _sidecarManager.DidNotReceive().StopSidecarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_ContinuesOnContainerStopFailure()
    {
        _containerRuntime.StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("stop failed"));

        // Should not throw; sidecar cleanup should still happen.
        await _manager.TeardownAsync("app-container", "sidecar-container", "test-network", TestContext.Current.CancellationToken);

        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-container", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TeardownAsync_ContinuesOnSidecarStopFailure()
    {
        _sidecarManager.StopSidecarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("sidecar stop failed"));

        // Should not throw.
        var act = () => _manager.TeardownAsync("app-container", "sidecar-container", "test-network", TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TeardownAsync_AllNull_DoesNothing()
    {
        await _manager.TeardownAsync(null, null, null, TestContext.Current.CancellationToken);

        await _containerRuntime.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sidecarManager.DidNotReceive().StopSidecarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
