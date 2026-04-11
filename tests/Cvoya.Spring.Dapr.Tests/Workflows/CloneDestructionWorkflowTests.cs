// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using FluentAssertions;

using global::Dapr.Workflow;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="CloneDestructionWorkflow"/>.
/// </summary>
public class CloneDestructionWorkflowTests
{
    private readonly WorkflowContext _context;
    private readonly CloneDestructionWorkflow _workflow;

    public CloneDestructionWorkflowTests()
    {
        _context = Substitute.For<WorkflowContext>();
        _workflow = new CloneDestructionWorkflow();
    }

    [Fact]
    public async Task RunAsync_EphemeralNoMemory_SkipsMemoryFlowAndDestroys()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        _context.CallActivityAsync<bool>(nameof(DestroyCloneActivity), input)
            .Returns(true);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeTrue();
        result.CloneId.Should().Be("clone-1");
        await _context.DidNotReceive().CallActivityAsync<bool>(
            nameof(FlowMemoryToParentActivity), Arg.Any<object>());
    }

    [Fact]
    public async Task RunAsync_EphemeralWithMemory_FlowsMemoryThenDestroys()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        _context.CallActivityAsync<bool>(nameof(FlowMemoryToParentActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(DestroyCloneActivity), input)
            .Returns(true);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeTrue();
        result.CloneId.Should().Be("clone-1");
        await _context.Received(1).CallActivityAsync<bool>(
            nameof(FlowMemoryToParentActivity), input);
        await _context.Received(1).CallActivityAsync<bool>(
            nameof(DestroyCloneActivity), input);
    }

    [Fact]
    public async Task RunAsync_MemoryFlowFails_ReturnsFailure()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        _context.CallActivityAsync<bool>(nameof(FlowMemoryToParentActivity), input)
            .Returns(false);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("memory");
        await _context.DidNotReceive().CallActivityAsync<bool>(
            nameof(DestroyCloneActivity), Arg.Any<object>());
    }

    [Fact]
    public async Task RunAsync_DestroyFails_ReturnsFailure()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        _context.CallActivityAsync<bool>(nameof(DestroyCloneActivity), input)
            .Returns(false);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("destroy");
    }
}