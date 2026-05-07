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
/// Unit tests for <see cref="CreateCloneActorActivity"/>.
/// </summary>
public class CreateCloneActorActivityTests
{
    private readonly IStateStore _stateStore;
    private readonly CreateCloneActorActivity _activity;
    private readonly WorkflowActivityContext _context;

    public CreateCloneActorActivityTests()
    {
        _stateStore = Substitute.For<IStateStore>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new CreateCloneActorActivity(_stateStore, loggerFactory);
        _context = Substitute.For<WorkflowActivityContext>();
    }

    [Fact]
    public async Task RunAsync_StoresCloneIdentity()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).SetAsync(
            $"clone-1:{StateKeys.CloneIdentity}",
            Arg.Is<CloneIdentity>(ci =>
                ci.ParentAgentId == "parent-agent" &&
                ci.CloneId == "clone-1" &&
                ci.CloningPolicy == CloningPolicy.EphemeralNoMemory &&
                ci.AttachmentMode == AttachmentMode.Detached),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AddsCloneToParentChildrenList()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        _stateStore.GetAsync<List<string>>(
            $"parent-agent:{StateKeys.CloneChildren}", Arg.Any<CancellationToken>())
            .Returns((List<string>?)null);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).SetAsync(
            $"parent-agent:{StateKeys.CloneChildren}",
            Arg.Is<List<string>>(list => list.Contains("clone-1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_EphemeralNoMemory_DoesNotCopyMemoryState()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        await _activity.RunAsync(_context, input);

        // Should not read active conversation or initiative state for copy.
        await _stateStore.DidNotReceive().GetAsync<object>(
            $"parent-agent:{StateKeys.ActiveConversation}", Arg.Any<CancellationToken>());
        await _stateStore.DidNotReceive().GetAsync<object>(
            $"parent-agent:{StateKeys.InitiativeState}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_EphemeralWithMemory_CopiesMemoryState()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        var activeConversation = new object();
        var initiativeState = new object();

        _stateStore.GetAsync<object>(
            $"parent-agent:{StateKeys.ActiveConversation}", Arg.Any<CancellationToken>())
            .Returns(activeConversation);
        _stateStore.GetAsync<object>(
            $"parent-agent:{StateKeys.InitiativeState}", Arg.Any<CancellationToken>())
            .Returns(initiativeState);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).SetAsync(
            $"clone-1:{StateKeys.ActiveConversation}",
            activeConversation,
            Arg.Any<CancellationToken>());
        await _stateStore.Received(1).SetAsync(
            $"clone-1:{StateKeys.InitiativeState}",
            initiativeState,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CopiesParentDefinition()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var definition = new object();
        _stateStore.GetAsync<object>(
            $"parent-agent:{StateKeys.AgentDefinition}", Arg.Any<CancellationToken>())
            .Returns(definition);

        await _activity.RunAsync(_context, input);

        await _stateStore.Received(1).SetAsync(
            $"clone-1:{StateKeys.AgentDefinition}",
            definition,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ReturnsTrue()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var result = await _activity.RunAsync(_context, input);

        result.ShouldBeTrue();
    }
}