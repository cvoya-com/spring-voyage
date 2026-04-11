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
/// Unit tests for <see cref="CloningLifecycleWorkflow"/>.
/// Tests verify the workflow orchestration logic by mocking the <see cref="WorkflowContext"/>.
/// </summary>
public class CloningLifecycleWorkflowTests
{
    private readonly WorkflowContext _context;
    private readonly CloningLifecycleWorkflow _workflow;

    public CloningLifecycleWorkflowTests()
    {
        _context = Substitute.For<WorkflowContext>();
        _workflow = new CloningLifecycleWorkflow();
    }

    [Fact]
    public async Task RunAsync_AllActivitiesSucceed_ReturnsSuccess()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        _context.CallActivityAsync<bool>(nameof(ValidateCloneRequestActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(CreateCloneActorActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(RegisterCloneActivity), input)
            .Returns(true);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeTrue();
        result.CloneId.Should().Be("clone-1");
        result.CloneAgentAddress.Should().Be("agent/clone-1");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_ValidationFails_ReturnsFailure()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        _context.CallActivityAsync<bool>(nameof(ValidateCloneRequestActivity), input)
            .Returns(false);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("validation failed");
        result.CloneId.Should().BeNull();
        result.CloneAgentAddress.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_ValidationFails_DoesNotCreateOrRegister()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        _context.CallActivityAsync<bool>(nameof(ValidateCloneRequestActivity), input)
            .Returns(false);

        await _workflow.RunAsync(_context, input);

        await _context.DidNotReceive().CallActivityAsync<bool>(
            nameof(CreateCloneActorActivity), Arg.Any<object>());
        await _context.DidNotReceive().CallActivityAsync<bool>(
            nameof(RegisterCloneActivity), Arg.Any<object>());
    }

    [Fact]
    public async Task RunAsync_CreateFails_ReturnsFailure()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        _context.CallActivityAsync<bool>(nameof(ValidateCloneRequestActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(CreateCloneActorActivity), input)
            .Returns(false);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("create clone actor");
    }

    [Fact]
    public async Task RunAsync_CreateFails_DoesNotRegister()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached);

        _context.CallActivityAsync<bool>(nameof(ValidateCloneRequestActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(CreateCloneActorActivity), input)
            .Returns(false);

        await _workflow.RunAsync(_context, input);

        await _context.DidNotReceive().CallActivityAsync<bool>(
            nameof(RegisterCloneActivity), Arg.Any<object>());
    }

    [Fact]
    public async Task RunAsync_RegisterFails_ReturnsFailure()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        _context.CallActivityAsync<bool>(nameof(ValidateCloneRequestActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(CreateCloneActorActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(RegisterCloneActivity), input)
            .Returns(false);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("register clone");
    }

    [Fact]
    public async Task RunAsync_WithMemoryPolicy_PassesInputToActivities()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralWithMemory, AttachmentMode.Attached,
            Budget: 100m, MaxClones: 5);

        _context.CallActivityAsync<bool>(nameof(ValidateCloneRequestActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(CreateCloneActorActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(RegisterCloneActivity), input)
            .Returns(true);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeTrue();
        await _context.Received(1).CallActivityAsync<bool>(
            nameof(ValidateCloneRequestActivity), input);
        await _context.Received(1).CallActivityAsync<bool>(
            nameof(CreateCloneActorActivity), input);
        await _context.Received(1).CallActivityAsync<bool>(
            nameof(RegisterCloneActivity), input);
    }
}