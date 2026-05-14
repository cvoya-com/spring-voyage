// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="FlowMemoryToParentActivity"/>.
/// </summary>
public class FlowMemoryToParentActivityTests
{
    private readonly IStateStore _stateStore;
    private readonly FlowMemoryToParentActivity _activity;
    private readonly WorkflowActivityContext _context;

    public FlowMemoryToParentActivityTests()
    {
        _stateStore = Substitute.For<IStateStore>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new FlowMemoryToParentActivity(_stateStore, loggerFactory);
        _context = Substitute.For<WorkflowActivityContext>();
    }

    [Fact]
    public async Task RunAsync_CopiesPerThreadChannelsToParent()
    {
        // #2076 / ADR-0030 §3 §44: per-thread channels keyed by
        // Agent:Channel:{ThreadId} replace the legacy Agent:ActiveThread
        // slot. The activity now copies every channel listed in the
        // clone's Agent:ChannelIndex.
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        var channelData = new object();
        _stateStore.GetAsync<List<string>>(
            $"clone-1:{StateKeys.ChannelIndex}", Arg.Any<CancellationToken>())
            .Returns(["thread-a"]);
        _stateStore.GetAsync<object>(
            $"clone-1:{StateKeys.ChannelPrefix}thread-a", Arg.Any<CancellationToken>())
            .Returns(channelData);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).SetAsync(
            $"parent-agent:{StateKeys.ChannelIndex}",
            Arg.Any<List<string>>(),
            Arg.Any<CancellationToken>());
        await _stateStore.Received(1).SetAsync(
            $"parent-agent:{StateKeys.ChannelPrefix}thread-a",
            channelData,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CopiesInitiativeStateToParent()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        var initiativeData = new object();
        _stateStore.GetAsync<object>(
            $"clone-1:{StateKeys.InitiativeState}", Arg.Any<CancellationToken>())
            .Returns(initiativeData);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).SetAsync(
            $"parent-agent:{StateKeys.InitiativeState}",
            initiativeData,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoCloneState_SkipsCopy()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        _stateStore.GetAsync<object>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((object?)null);

        await _activity.RunAsync(_context, input);

        await _stateStore.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ReturnsTrue()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        var result = await _activity.RunAsync(_context, input);

        result.ShouldBeTrue();
    }
}
