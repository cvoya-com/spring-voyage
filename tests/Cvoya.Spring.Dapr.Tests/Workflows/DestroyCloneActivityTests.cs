// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using FluentAssertions;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DestroyCloneActivity"/>.
/// </summary>
public class DestroyCloneActivityTests
{
    private readonly IDirectoryService _directoryService;
    private readonly IStateStore _stateStore;
    private readonly DestroyCloneActivity _activity;
    private readonly WorkflowActivityContext _context;

    public DestroyCloneActivityTests()
    {
        _directoryService = Substitute.For<IDirectoryService>();
        _stateStore = Substitute.For<IStateStore>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new DestroyCloneActivity(_directoryService, _stateStore, loggerFactory);
        _context = Substitute.For<WorkflowActivityContext>();
    }

    [Fact]
    public async Task RunAsync_UnregistersFromDirectory()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        await _activity.RunAsync(_context, input);

        await _directoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "clone-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CleansUpCloneState()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).DeleteAsync(
            $"clone-1:{StateKeys.CloneIdentity}", Arg.Any<CancellationToken>());
        await _stateStore.Received(1).DeleteAsync(
            $"clone-1:{StateKeys.AgentDefinition}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RemovesFromParentChildrenList()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var children = new List<string> { "clone-1", "clone-2" };
        _stateStore.GetAsync<List<string>>(
            $"parent-agent:{StateKeys.CloneChildren}", Arg.Any<CancellationToken>())
            .Returns(children);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).SetAsync(
            $"parent-agent:{StateKeys.CloneChildren}",
            Arg.Is<List<string>>(list => !list.Contains("clone-1") && list.Contains("clone-2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ReturnsTrue()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeTrue();
    }
}