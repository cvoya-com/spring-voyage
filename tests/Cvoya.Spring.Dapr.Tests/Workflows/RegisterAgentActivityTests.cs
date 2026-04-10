// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;
using global::Dapr.Workflow;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="RegisterAgentActivity"/>.
/// </summary>
public class RegisterAgentActivityTests
{
    private readonly IDirectoryService _directoryService;
    private readonly RegisterAgentActivity _activity;

    public RegisterAgentActivityTests()
    {
        _directoryService = Substitute.For<IDirectoryService>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new RegisterAgentActivity(_directoryService, loggerFactory);
    }

    [Fact]
    public async Task RunAsync_CallsDirectoryServiceWithCorrectEntry()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Create, "agent-1", AgentName: "Ada", Role: "backend-engineer");
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.Should().BeTrue();
        await _directoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.Address.Path == "agent-1" &&
                e.ActorId == "agent-1" &&
                e.DisplayName == "Ada" &&
                e.Role == "backend-engineer"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_UsesAgentIdAsDisplayName_WhenAgentNameIsNull()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Create, "agent-1");
        var context = Substitute.For<WorkflowActivityContext>();

        await _activity.RunAsync(context, input);

        await _directoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.DisplayName == "agent-1"),
            Arg.Any<CancellationToken>());
    }
}
