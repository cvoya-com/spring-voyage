// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="EphemeralAgentRegistry"/>. Mirrors the
/// per-conversation lease semantics used by <see cref="A2AExecutionDispatcher"/>'s
/// unified ephemeral path (PR 5 of the #1087 series).
/// </summary>
public class EphemeralAgentRegistryTests
{
    private readonly IContainerRuntime _runtime = Substitute.For<IContainerRuntime>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly EphemeralAgentRegistry _registry;

    public EphemeralAgentRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _registry = new EphemeralAgentRegistry(_runtime, _loggerFactory);
    }

    [Fact]
    public void Register_TracksEntry()
    {
        var lease = _registry.Register("agent-1", "conv-1", "container-1");

        lease.Token.ShouldNotBeNullOrEmpty();
        var entries = _registry.GetAllEntries();
        entries.Count.ShouldBe(1);
        entries.Single().AgentId.ShouldBe("agent-1");
        entries.Single().ContainerId.ShouldBe("container-1");
    }

    [Fact]
    public async Task ReleaseAsync_RemovesEntryAndStopsContainer()
    {
        var lease = _registry.Register("agent-1", "conv-1", "container-1");

        await _registry.ReleaseAsync(lease, TestContext.Current.CancellationToken);

        await _runtime.Received(1).StopAsync("container-1", Arg.Any<CancellationToken>());
        _registry.GetAllEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_Twice_IsIdempotent()
    {
        var lease = _registry.Register("agent-1", "conv-1", "container-1");

        await _registry.ReleaseAsync(lease, TestContext.Current.CancellationToken);
        await _registry.ReleaseAsync(lease, TestContext.Current.CancellationToken);

        await _runtime.Received(1).StopAsync("container-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_StopsAllTrackedContainers()
    {
        _registry.Register("a", "c1", "container-a");
        _registry.Register("b", "c2", "container-b");

        await _registry.StopAsync(TestContext.Current.CancellationToken);

        await _runtime.Received(1).StopAsync("container-a", Arg.Any<CancellationToken>());
        await _runtime.Received(1).StopAsync("container-b", Arg.Any<CancellationToken>());
        _registry.GetAllEntries().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_StopFailure_DoesNotThrow()
    {
        var lease = _registry.Register("agent-1", "conv-1", "container-1");
        _runtime.When(r => r.StopAsync("container-1", Arg.Any<CancellationToken>()))
            .Throw(new InvalidOperationException("docker daemon down"));

        // Failure to stop must still drop the entry — operator intent is
        // "this lease is over"; a leaked container is recoverable via the
        // runtime's own cleanup tools.
        await _registry.ReleaseAsync(lease, TestContext.Current.CancellationToken);

        _registry.GetAllEntries().ShouldBeEmpty();
    }
}