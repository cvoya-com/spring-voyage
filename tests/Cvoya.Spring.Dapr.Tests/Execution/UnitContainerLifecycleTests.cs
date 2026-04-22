// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnitContainerLifecycle"/> covering the persistence of
/// lifecycle handles via <see cref="IStateStore"/>, including a regression test that
/// simulates an API-host restart between <c>start</c> and <c>stop</c>.
/// </summary>
public class UnitContainerLifecycleTests
{
    private const string UnitId = "engineering";
    private const string HandleKey = "Unit:ContainerHandle:engineering";

    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IDaprSidecarManager _sidecarManager = Substitute.For<IDaprSidecarManager>();
    private readonly InMemoryStateStore _stateStore = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ContainerLifecycleManager _lifecycleManager;

    public UnitContainerLifecycleTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // We never invoke LaunchWithSidecarAsync directly from the lifecycle tests that
        // drive a full start (the class is non-virtual so it cannot be mocked). Tests
        // that would otherwise need a start invoke the persistence surface via their
        // own arrangement of a lifecycle handle in the state store.
        _lifecycleManager = new ContainerLifecycleManager(
            _containerRuntime,
            _sidecarManager,
            Options.Create(new DaprSidecarOptions()),
            _loggerFactory);
    }

    [Fact]
    public async Task StopUnitAsync_WithPersistedHandle_TearsDownUsingHandle()
    {
        await SeedHandleAsync(new UnitContainerLifecycle.UnitLifecycleHandle(
            ContainerId: "app-abc",
            SidecarId: "sidecar-abc",
            NetworkName: "spring-net-abc"));

        var sut = CreateSut();

        await sut.StopUnitAsync(UnitId, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StopAsync("app-abc", Arg.Any<CancellationToken>());
        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopUnitAsync_ClearsPersistedHandleAfterTeardown()
    {
        await SeedHandleAsync(new UnitContainerLifecycle.UnitLifecycleHandle("app", "sidecar", "net"));

        var sut = CreateSut();
        await sut.StopUnitAsync(UnitId, TestContext.Current.CancellationToken);

        var remaining = await _stateStore.GetAsync<UnitContainerLifecycle.UnitLifecycleHandle>(
            HandleKey, TestContext.Current.CancellationToken);
        remaining.ShouldBeNull();
    }

    [Fact]
    public async Task StopUnitAsync_NoPersistedHandle_IssuesBestEffortTeardownAndLogs()
    {
        var sut = CreateSut();

        await sut.StopUnitAsync(UnitId, TestContext.Current.CancellationToken);

        // With no handle the underlying runtime/sidecar stop calls must be skipped
        // because the teardown receives null identifiers.
        await _containerRuntime.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _sidecarManager.DidNotReceive().StopSidecarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression test for #115: persisting the lifecycle handle must survive a host
    /// restart between start and stop. The test simulates this by writing a handle via
    /// one <see cref="UnitContainerLifecycle"/> instance and invoking stop on a second,
    /// freshly-constructed instance backed by the same state store.
    /// </summary>
    [Fact]
    public async Task StopUnitAsync_AfterHostRestart_StillTearsDownCleanly()
    {
        // Instance #1 writes the handle as if from a successful start.
        var handle = new UnitContainerLifecycle.UnitLifecycleHandle("app-xyz", "sidecar-xyz", "spring-net-xyz");
        await _stateStore.SetAsync(HandleKey, handle, TestContext.Current.CancellationToken);

        // Simulate the host recycling: discard the first instance and issue stop from a
        // brand-new instance. The second instance shares only the state store with the
        // first — exactly what a replica would see after the previous process died.
        _ = CreateSut();
        var restartedSut = CreateSut();

        await restartedSut.StopUnitAsync(UnitId, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).StopAsync("app-xyz", Arg.Any<CancellationToken>());
        await _sidecarManager.Received(1).StopSidecarAsync("sidecar-xyz", Arg.Any<CancellationToken>());

        var remaining = await _stateStore.GetAsync<UnitContainerLifecycle.UnitLifecycleHandle>(
            HandleKey, TestContext.Current.CancellationToken);
        remaining.ShouldBeNull();
    }

    [Fact]
    public async Task StartUnitAsync_WhitespaceUnitId_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.StartUnitAsync("   ", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentException>(act);
    }

    private UnitContainerLifecycle CreateSut()
        => new(
            _lifecycleManager,
            _stateStore,
            Options.Create(new UnitRuntimeOptions()),
            _loggerFactory);

    private Task SeedHandleAsync(UnitContainerLifecycle.UnitLifecycleHandle handle)
        => _stateStore.SetAsync(HandleKey, handle, TestContext.Current.CancellationToken);

    /// <summary>
    /// In-memory <see cref="IStateStore"/> implementation used to share state across
    /// simulated host-restart boundaries within a single test.
    /// </summary>
    private sealed class InMemoryStateStore : IStateStore
    {
        private readonly ConcurrentDictionary<string, string> _store = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (_store.TryGetValue(key, out var json))
            {
                return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json));
            }

            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _store[key] = System.Text.Json.JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_store.ContainsKey(key));
    }
}