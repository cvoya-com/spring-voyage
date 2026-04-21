// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PullImageActivity"/>.
/// </summary>
public class PullImageActivityTests
{
    private readonly IContainerRuntime _containerRuntime;
    private readonly PullImageActivity _activity;

    public PullImageActivityTests()
    {
        _containerRuntime = Substitute.For<IContainerRuntime>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new PullImageActivity(_containerRuntime, loggerFactory);
    }

    [Fact]
    public async Task RunAsync_PullSucceeds_ReturnsSuccess()
    {
        var input = new PullImageActivityInput("ghcr.io/cvoya/claude:1", TimeSpan.FromMinutes(1));
        _containerRuntime.PullImageAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.Success.ShouldBeTrue();
        result.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_PullThrowsInvalidOperation_ReturnsImagePullFailed()
    {
        var input = new PullImageActivityInput("ghcr.io/cvoya/claude:1", TimeSpan.FromMinutes(1));
        _containerRuntime.PullImageAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("auth failed"));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.Success.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Step.ShouldBe(UnitValidationStep.PullingImage);
        result.Failure.Code.ShouldBe(UnitValidationCodes.ImagePullFailed);
        result.Failure.Message.ShouldContain("auth failed");
    }

    [Fact]
    public async Task RunAsync_PullTimesOut_ReturnsProbeTimeout()
    {
        var input = new PullImageActivityInput("ghcr.io/cvoya/claude:1", TimeSpan.FromSeconds(5));
        _containerRuntime.PullImageAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Throws(new TimeoutException("Pull of image ghcr.io/cvoya/claude:1 exceeded timeout of 00:00:05."));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.Success.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Step.ShouldBe(UnitValidationStep.PullingImage);
        result.Failure.Code.ShouldBe(UnitValidationCodes.ProbeTimeout);
    }

    [Fact]
    public async Task RunAsync_FailureDetails_CarryImageReference()
    {
        var input = new PullImageActivityInput("ghcr.io/cvoya/claude:1", TimeSpan.FromMinutes(1));
        _containerRuntime.PullImageAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("registry 500"));
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.Failure!.Details.ShouldNotBeNull();
        result.Failure.Details!["image"].ShouldBe("ghcr.io/cvoya/claude:1");
    }
}