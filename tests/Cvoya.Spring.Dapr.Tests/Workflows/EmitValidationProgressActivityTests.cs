// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="EmitValidationProgressActivity"/>.
/// </summary>
public class EmitValidationProgressActivityTests
{
    private static readonly Guid UnitId = TestSlugIds.For("unit-1");
    private static readonly string UnitHex = TestSlugIds.HexFor("unit-1");

    private readonly IActivityEventBus _bus;
    private readonly EmitValidationProgressActivity _activity;

    public EmitValidationProgressActivityTests()
    {
        _bus = Substitute.For<IActivityEventBus>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activity = new EmitValidationProgressActivity(_bus, loggerFactory);
    }

    [Fact]
    public async Task RunAsync_PublishesValidationProgressEvent_WithUnitAddress()
    {
        var input = new EmitValidationProgressActivityInput(
            UnitId: UnitId,
            Step: UnitValidationStep.VerifyingTool,
            Status: "Running",
            Code: null);
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.ShouldBeTrue();
        await _bus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ValidationProgress &&
                e.Source.Scheme == "unit" &&
                e.Source.Path == UnitHex),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RunningStatus_UsesInfoSeverity()
    {
        var input = new EmitValidationProgressActivityInput(
            UnitId, UnitValidationStep.VerifyingTool, "Running", null);
        var context = Substitute.For<WorkflowActivityContext>();

        await _activity.RunAsync(context, input);

        await _bus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Info),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_FailedStatus_UsesWarningSeverity()
    {
        var input = new EmitValidationProgressActivityInput(
            UnitId, UnitValidationStep.VerifyingTool, "Failed", UnitValidationCodes.ToolMissing);
        var context = Substitute.For<WorkflowActivityContext>();

        await _activity.RunAsync(context, input);

        await _bus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Warning),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_DetailsCarryStepAndStatus()
    {
        var input = new EmitValidationProgressActivityInput(
            UnitId, UnitValidationStep.ResolvingModel, "Succeeded", null);
        var context = Substitute.For<WorkflowActivityContext>();

        ActivityEvent? captured = null;
        await _bus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e),
            Arg.Any<CancellationToken>());

        await _activity.RunAsync(context, input);

        captured.ShouldNotBeNull();
        captured!.Details.ShouldNotBeNull();
        var details = captured.Details!.Value;
        details.GetProperty("step").GetString().ShouldBe("ResolvingModel");
        details.GetProperty("status").GetString().ShouldBe("Succeeded");
        // No code when not failed.
        details.TryGetProperty("code", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_FailureDetailsIncludeCode()
    {
        var input = new EmitValidationProgressActivityInput(
            UnitId,
            UnitValidationStep.ValidatingCredential,
            "Failed",
            UnitValidationCodes.CredentialInvalid);
        var context = Substitute.For<WorkflowActivityContext>();

        ActivityEvent? captured = null;
        await _bus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e),
            Arg.Any<CancellationToken>());

        await _activity.RunAsync(context, input);

        captured.ShouldNotBeNull();
        var details = captured!.Details!.Value;
        details.GetProperty("code").GetString().ShouldBe(UnitValidationCodes.CredentialInvalid);
    }

    [Fact]
    public async Task RunAsync_BusThrows_ReturnsFalse_DoesNotThrow()
    {
        _bus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bus down")));
        var input = new EmitValidationProgressActivityInput(
            UnitId, UnitValidationStep.VerifyingTool, "Running", null);
        var context = Substitute.For<WorkflowActivityContext>();

        var result = await _activity.RunAsync(context, input);

        result.ShouldBeFalse();
    }
}