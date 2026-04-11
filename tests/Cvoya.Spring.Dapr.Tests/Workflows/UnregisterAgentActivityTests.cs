// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using FluentAssertions;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnregisterAgentActivity"/>.
/// </summary>
public class UnregisterAgentActivityTests
{
    private readonly IDirectoryService _directoryService;
    private readonly UnregisterAgentActivity _activity;

    public UnregisterAgentActivityTests()
    {
        _directoryService = Substitute.For<IDirectoryService>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new UnregisterAgentActivity(_directoryService, loggerFactory);
    }

    [Fact]
    public async Task RunAsync_CallsDirectoryServiceUnregister()
    {
        var input = new AgentLifecycleInput(
            LifecycleOperation.Delete, "agent-1");
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.Should().BeTrue();
        await _directoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "agent-1"),
            Arg.Any<CancellationToken>());
    }
}