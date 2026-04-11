// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using FluentAssertions;

using global::Dapr.Workflow;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentLifecycleWorkflow"/>.
/// Tests verify the workflow orchestration logic by mocking the <see cref="WorkflowContext"/>.
/// </summary>
public class AgentLifecycleWorkflowTests
{
    private readonly WorkflowContext _context;
    private readonly AgentLifecycleWorkflow _workflow;

    public AgentLifecycleWorkflowTests()
    {
        _context = Substitute.For<WorkflowContext>();
        _workflow = new AgentLifecycleWorkflow();
    }

    [Fact]
    public async Task RunAsync_CreateOperation_ValidationPasses_ReturnsSuccess()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Create, "agent-1", AgentName: "Ada", Role: "backend-engineer");

        _context.CallActivityAsync<bool>(nameof(ValidateAgentDefinitionActivity), input)
            .Returns(true);
        _context.CallActivityAsync<bool>(nameof(RegisterAgentActivity), input)
            .Returns(true);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeTrue();
        result.AgentAddress.Should().Be("agent://agent-1");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_CreateOperation_ValidationFails_ReturnsFailure()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Create, "", AgentName: "Ada");

        _context.CallActivityAsync<bool>(nameof(ValidateAgentDefinitionActivity), input)
            .Returns(false);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Validation failed");
        result.AgentAddress.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_CreateOperation_ValidationFails_DoesNotRegister()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Create, "", AgentName: "Ada");

        _context.CallActivityAsync<bool>(nameof(ValidateAgentDefinitionActivity), input)
            .Returns(false);

        await _workflow.RunAsync(_context, input);

        await _context.DidNotReceive().CallActivityAsync<bool>(
            nameof(RegisterAgentActivity), Arg.Any<object>());
    }

    [Fact]
    public async Task RunAsync_DeleteOperation_ReturnsSuccess()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Delete, "agent-1");

        _context.CallActivityAsync<bool>(nameof(UnregisterAgentActivity), input)
            .Returns(true);

        var result = await _workflow.RunAsync(_context, input);

        result.Success.Should().BeTrue();
        result.AgentAddress.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_UnknownOperation_ThrowsSpringException()
    {
        var input = new AgentLifecycleInput(
            (LifecycleOperation)99, "agent-1");

        var act = () => _workflow.RunAsync(_context, input);

        await act.Should().ThrowAsync<SpringException>()
            .WithMessage("*Unknown lifecycle operation*");
    }
}