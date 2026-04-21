// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnitValidationWorkflow"/>. The workflow's
/// orchestration is validated by substituting the
/// <see cref="WorkflowContext"/> and asserting which activities it calls
/// and in what order.
/// </summary>
public class UnitValidationWorkflowTests
{
    private readonly WorkflowContext _context;
    private readonly UnitValidationWorkflow _workflow;
    private readonly List<EmitValidationProgressActivityInput> _emitted = new();

    public UnitValidationWorkflowTests()
    {
        _context = Substitute.For<WorkflowContext>();
        _workflow = new UnitValidationWorkflow();

        // Capture every EmitValidationProgressActivity call so tests can
        // assert on the event sequence emitted by the workflow.
        _context.CallActivityAsync<bool>(
                nameof(EmitValidationProgressActivity),
                Arg.Do<object?>(o =>
                {
                    if (o is EmitValidationProgressActivityInput e)
                    {
                        _emitted.Add(e);
                    }
                }))
            .Returns(true);
    }

    private static UnitValidationWorkflowInput Input(string model = "gpt-4o") =>
        new(
            UnitId: "unit-1",
            Image: "ghcr.io/cvoya/test:1",
            RuntimeId: "test-runtime",
            Credential: "sk-test",
            RequestedModel: model);

    private void SetupPullImage(bool success, UnitValidationError? failure = null)
    {
        _context.CallActivityAsync<PullImageActivityOutput>(
                nameof(PullImageActivity), Arg.Any<object?>())
            .Returns(new PullImageActivityOutput(success, failure));
    }

    private void SetupProbeStep(UnitValidationStep step, RunContainerProbeActivityOutput output)
    {
        _context.CallActivityAsync<RunContainerProbeActivityOutput>(
                nameof(RunContainerProbeActivity),
                Arg.Is<object?>(o => MatchesStep(o, step)))
            .Returns(output);
    }

    private static bool MatchesStep(object? o, UnitValidationStep expected) =>
        o is RunContainerProbeActivityInput input && input.Step == expected;

    private static RunContainerProbeActivityOutput Succeeded(
        IReadOnlyDictionary<string, string>? extras = null) =>
        new(Success: true, Failure: null, Extras: extras,
            RedactedStdOut: string.Empty, RedactedStdErr: string.Empty);

    private static RunContainerProbeActivityOutput Failed(UnitValidationStep step, string code) =>
        new(
            Success: false,
            Failure: new UnitValidationError(step, code, "failed", null),
            Extras: null,
            RedactedStdOut: string.Empty,
            RedactedStdErr: string.Empty);

    [Fact]
    public async Task RunAsync_AllStepsPass_ReturnsSuccessWithLiveModels()
    {
        SetupPullImage(success: true);
        SetupProbeStep(UnitValidationStep.VerifyingTool, Succeeded());
        SetupProbeStep(UnitValidationStep.ValidatingCredential, Succeeded());
        SetupProbeStep(
            UnitValidationStep.ResolvingModel,
            Succeeded(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["models"] = "gpt-4o,gpt-4o-mini",
            }));

        var result = await _workflow.RunAsync(_context, Input());

        result.Success.ShouldBeTrue();
        result.Failure.ShouldBeNull();
        result.LiveModels.ShouldNotBeNull();
        result.LiveModels!.ShouldBe(new[] { "gpt-4o", "gpt-4o-mini" });
    }

    [Fact]
    public async Task RunAsync_PullFails_ReturnsImagePullFailed_DoesNotRunProbes()
    {
        SetupPullImage(
            success: false,
            failure: new UnitValidationError(
                UnitValidationStep.PullingImage,
                UnitValidationCodes.ImagePullFailed,
                "registry denied",
                null));

        var result = await _workflow.RunAsync(_context, Input());

        result.Success.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Code.ShouldBe(UnitValidationCodes.ImagePullFailed);
        result.Failure.Step.ShouldBe(UnitValidationStep.PullingImage);

        await _context.DidNotReceive().CallActivityAsync<RunContainerProbeActivityOutput>(
            nameof(RunContainerProbeActivity), Arg.Any<object?>());
    }

    [Fact]
    public async Task RunAsync_VerifyingToolFails_ReturnsToolMissing_SkipsLaterSteps()
    {
        SetupPullImage(success: true);
        SetupProbeStep(
            UnitValidationStep.VerifyingTool,
            Failed(UnitValidationStep.VerifyingTool, UnitValidationCodes.ToolMissing));

        var result = await _workflow.RunAsync(_context, Input());

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(UnitValidationCodes.ToolMissing);
        result.Failure.Step.ShouldBe(UnitValidationStep.VerifyingTool);

        // No ValidatingCredential / ResolvingModel should have fired.
        await _context.DidNotReceive().CallActivityAsync<RunContainerProbeActivityOutput>(
            nameof(RunContainerProbeActivity),
            Arg.Is<object?>(o => MatchesStep(o, UnitValidationStep.ValidatingCredential)));
        await _context.DidNotReceive().CallActivityAsync<RunContainerProbeActivityOutput>(
            nameof(RunContainerProbeActivity),
            Arg.Is<object?>(o => MatchesStep(o, UnitValidationStep.ResolvingModel)));
    }

    [Fact]
    public async Task RunAsync_CredentialInvalid_ReturnsCredentialInvalid_SkipsResolvingModel()
    {
        SetupPullImage(success: true);
        SetupProbeStep(UnitValidationStep.VerifyingTool, Succeeded());
        SetupProbeStep(
            UnitValidationStep.ValidatingCredential,
            Failed(UnitValidationStep.ValidatingCredential, UnitValidationCodes.CredentialInvalid));

        var result = await _workflow.RunAsync(_context, Input());

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(UnitValidationCodes.CredentialInvalid);
        result.Failure.Step.ShouldBe(UnitValidationStep.ValidatingCredential);

        await _context.DidNotReceive().CallActivityAsync<RunContainerProbeActivityOutput>(
            nameof(RunContainerProbeActivity),
            Arg.Is<object?>(o => MatchesStep(o, UnitValidationStep.ResolvingModel)));
    }

    [Fact]
    public async Task RunAsync_ModelNotFound_ReturnsModelNotFound()
    {
        SetupPullImage(success: true);
        SetupProbeStep(UnitValidationStep.VerifyingTool, Succeeded());
        SetupProbeStep(UnitValidationStep.ValidatingCredential, Succeeded());
        SetupProbeStep(
            UnitValidationStep.ResolvingModel,
            Failed(UnitValidationStep.ResolvingModel, UnitValidationCodes.ModelNotFound));

        var result = await _workflow.RunAsync(_context, Input());

        result.Success.ShouldBeFalse();
        result.Failure!.Code.ShouldBe(UnitValidationCodes.ModelNotFound);
        result.Failure.Step.ShouldBe(UnitValidationStep.ResolvingModel);
        result.LiveModels.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_HappyPath_EmitsRunningAndSucceededForEveryStep()
    {
        SetupPullImage(success: true);
        SetupProbeStep(UnitValidationStep.VerifyingTool, Succeeded());
        SetupProbeStep(UnitValidationStep.ValidatingCredential, Succeeded());
        SetupProbeStep(UnitValidationStep.ResolvingModel, Succeeded());

        await _workflow.RunAsync(_context, Input());

        var steps = new[]
        {
            UnitValidationStep.PullingImage,
            UnitValidationStep.VerifyingTool,
            UnitValidationStep.ValidatingCredential,
            UnitValidationStep.ResolvingModel,
        };
        foreach (var step in steps)
        {
            _emitted.ShouldContain(e => e.Step == step && e.Status == "Running");
            _emitted.ShouldContain(e => e.Step == step && e.Status == "Succeeded");
        }

        // No Failed events on the happy path.
        _emitted.ShouldNotContain(e => e.Status == "Failed");
    }

    [Fact]
    public async Task RunAsync_PullFails_EmitsFailedWithCode()
    {
        SetupPullImage(
            success: false,
            failure: new UnitValidationError(
                UnitValidationStep.PullingImage,
                UnitValidationCodes.ImagePullFailed,
                "err",
                null));

        await _workflow.RunAsync(_context, Input());

        _emitted.ShouldContain(e =>
            e.Step == UnitValidationStep.PullingImage &&
            e.Status == "Failed" &&
            e.Code == UnitValidationCodes.ImagePullFailed);
    }

    [Fact]
    public async Task RunAsync_VerifyingToolFails_EmitsFailedWithCode()
    {
        SetupPullImage(success: true);
        SetupProbeStep(
            UnitValidationStep.VerifyingTool,
            Failed(UnitValidationStep.VerifyingTool, UnitValidationCodes.ToolMissing));

        await _workflow.RunAsync(_context, Input());

        _emitted.ShouldContain(e =>
            e.Step == UnitValidationStep.VerifyingTool &&
            e.Status == "Failed" &&
            e.Code == UnitValidationCodes.ToolMissing);
    }

    [Fact]
    public async Task RunAsync_SuccessWithoutModelExtras_ReturnsNullLiveModels()
    {
        SetupPullImage(success: true);
        SetupProbeStep(UnitValidationStep.VerifyingTool, Succeeded());
        SetupProbeStep(UnitValidationStep.ValidatingCredential, Succeeded());
        SetupProbeStep(
            UnitValidationStep.ResolvingModel,
            Succeeded(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Interpreter emitted a model key but no "models" list
                // (matches Claude's single-model confirmation shape).
                ["model"] = "claude-sonnet-4",
            }));

        var result = await _workflow.RunAsync(_context, Input());

        result.Success.ShouldBeTrue();
        result.LiveModels.ShouldBeNull();
    }
}