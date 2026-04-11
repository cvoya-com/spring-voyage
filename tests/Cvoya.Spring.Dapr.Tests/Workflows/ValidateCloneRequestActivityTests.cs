// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using FluentAssertions;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ValidateCloneRequestActivity"/>.
/// </summary>
public class ValidateCloneRequestActivityTests
{
    private readonly IStateStore _stateStore;
    private readonly ValidateCloneRequestActivity _activity;
    private readonly WorkflowActivityContext _context;

    public ValidateCloneRequestActivityTests()
    {
        _stateStore = Substitute.For<IStateStore>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new ValidateCloneRequestActivity(_stateStore, loggerFactory);
        _context = Substitute.For<WorkflowActivityContext>();
    }

    [Fact]
    public async Task RunAsync_ValidInput_ReturnsTrue()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_EmptySourceAgentId_ReturnsFalse()
    {
        var input = new CloningInput(
            "", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_EmptyTargetAgentId_ReturnsFalse()
    {
        var input = new CloningInput(
            "parent-agent", "",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CloningPolicyNone_ReturnsFalse()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.None, AttachmentMode.Detached);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_MaxClonesExceeded_ReturnsFalse()
    {
        var input = new CloningInput(
            "parent-agent", "clone-3",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            MaxClones: 2);

        _stateStore.GetAsync<List<string>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "clone-1", "clone-2" });

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_MaxClonesNotExceeded_ReturnsTrue()
    {
        var input = new CloningInput(
            "parent-agent", "clone-2",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            MaxClones: 3);

        _stateStore.GetAsync<List<string>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "clone-1" });

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ZeroBudget_ReturnsFalse()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            Budget: 0m);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_NegativeBudget_ReturnsFalse()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            Budget: -10m);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_PositiveBudget_ReturnsTrue()
    {
        var input = new CloningInput(
            "parent-agent", "clone-1",
            CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            Budget: 100m);

        var result = await _activity.RunAsync(_context, input);

        result.Should().BeTrue();
    }
}