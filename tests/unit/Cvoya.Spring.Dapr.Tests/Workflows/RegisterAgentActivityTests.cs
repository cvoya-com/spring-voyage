// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RegisterAgentActivity"/>. Post #1629: agent ids
/// are Guids — the test passes the no-dash hex form on input and asserts the
/// emitted <see cref="DirectoryEntry.ActorId"/> matches that Guid.
/// </summary>
public class RegisterAgentActivityTests
{
    private static readonly Guid AgentGuid = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly string AgentIdHex = AgentGuid.ToString("N");

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
            LifecycleOperation.Create, AgentIdHex, AgentName: "Ada", Role: "backend-engineer");
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.ShouldBeTrue();
        await _directoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.Address.Id == AgentGuid &&
                e.ActorId == AgentGuid &&
                e.DisplayName == "Ada" &&
                e.Role == "backend-engineer"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_UsesAgentIdAsDisplayName_WhenAgentNameIsNull()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Create, AgentIdHex);
        var context = Substitute.For<WorkflowActivityContext>();

        await _activity.RunAsync(context, input);

        await _directoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.DisplayName == AgentIdHex),
            Arg.Any<CancellationToken>());
    }
}
