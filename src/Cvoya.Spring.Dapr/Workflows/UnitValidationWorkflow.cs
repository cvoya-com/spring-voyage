// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Workflows.Activities;

using global::Dapr.Workflow;

/// <summary>
/// Dapr Workflow that validates a unit by pulling its container image,
/// running each probe step the unit's runtime declares, and emitting
/// <see cref="Cvoya.Spring.Core.Capabilities.ActivityEventType.ValidationProgress"/>
/// events as the probe advances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism.</b> Workflow bodies run inside the Dapr orchestrator and
/// must not capture non-serialisable delegates, inject DI services, or read
/// ambient state (<c>DateTime.Now</c>, random, etc.). Every side effect
/// (container exec, event publish, runtime resolution) is delegated to an
/// activity.
/// </para>
/// <para>
/// <b>Flow.</b>
/// <list type="number">
///   <item>Emit <c>ValidationProgress { PullingImage, Running }</c>.</item>
///   <item>Call <see cref="PullImageActivity"/>. Fail short-circuits with <see cref="UnitValidationCodes.ImagePullFailed"/> / <see cref="UnitValidationCodes.ProbeTimeout"/>.</item>
///   <item>For each <see cref="UnitValidationStep"/> in <c>VerifyingTool, ValidatingCredential, ResolvingModel</c>: emit <c>Running</c>, call <see cref="RunContainerProbeActivity"/>, on failure emit <c>Failed</c> + code and return, on success emit <c>Succeeded</c>.</item>
///   <item>After <see cref="UnitValidationStep.ResolvingModel"/> succeeds, extract any <c>"models"</c> extras key as the live model catalog.</item>
/// </list>
/// </para>
/// <para>
/// <b>Skipped steps.</b> Runtimes whose credential schema is
/// <see cref="Cvoya.Spring.Core.AgentRuntimes.AgentRuntimeCredentialKind.None"/>
/// (Ollama) omit <see cref="UnitValidationStep.ValidatingCredential"/> from
/// their step list; the activity layer returns a "no step declared" error
/// for a request that references a skipped step, so the workflow
/// pre-filters by driving the fixed ordinal sequence and asks the activity
/// only for steps it knows the runtime declared. T-04 hard-codes the
/// universal ordering; T-05+ may revisit.
/// </para>
/// </remarks>
public class UnitValidationWorkflow : Workflow<UnitValidationWorkflowInput, UnitValidationWorkflowOutput>
{
    private const string StatusRunning = "Running";
    private const string StatusSucceeded = "Succeeded";
    private const string StatusFailed = "Failed";

    private static readonly TimeSpan ImagePullTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ordered probe-step sequence the workflow walks after the image pull.
    /// Matches the contract in
    /// <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime.GetProbeSteps(Cvoya.Spring.Core.AgentRuntimes.AgentRuntimeInstallConfig, string)"/>:
    /// <see cref="UnitValidationStep.PullingImage"/> is the workflow's own
    /// first step and is not included here.
    /// </summary>
    private static readonly UnitValidationStep[] PostPullSteps =
    {
        UnitValidationStep.VerifyingTool,
        UnitValidationStep.ValidatingCredential,
        UnitValidationStep.ResolvingModel,
    };

    /// <inheritdoc />
    public override async Task<UnitValidationWorkflowOutput> RunAsync(
        WorkflowContext context, UnitValidationWorkflowInput input)
    {
        // Step 1: Pull the image.
        await EmitProgressAsync(context, input, UnitValidationStep.PullingImage, StatusRunning, code: null);

        var pullOutput = await context.CallActivityAsync<PullImageActivityOutput>(
            nameof(PullImageActivity),
            new PullImageActivityInput(input.Image, ImagePullTimeout));

        if (!pullOutput.Success)
        {
            await EmitProgressAsync(
                context,
                input,
                UnitValidationStep.PullingImage,
                StatusFailed,
                pullOutput.Failure?.Code);

            return new UnitValidationWorkflowOutput(
                Success: false,
                Failure: pullOutput.Failure,
                LiveModels: null);
        }

        await EmitProgressAsync(
            context, input, UnitValidationStep.PullingImage, StatusSucceeded, code: null);

        // Steps 2..N: walk each post-pull probe step in order.
        IReadOnlyList<string>? liveModels = null;

        foreach (var step in PostPullSteps)
        {
            await EmitProgressAsync(context, input, step, StatusRunning, code: null);

            var probeOutput = await context.CallActivityAsync<RunContainerProbeActivityOutput>(
                nameof(RunContainerProbeActivity),
                new RunContainerProbeActivityInput(
                    RuntimeId: input.RuntimeId,
                    Step: step,
                    Image: input.Image,
                    Credential: input.Credential,
                    RequestedModel: input.RequestedModel));

            if (!probeOutput.Success)
            {
                await EmitProgressAsync(context, input, step, StatusFailed, probeOutput.Failure?.Code);

                return new UnitValidationWorkflowOutput(
                    Success: false,
                    Failure: probeOutput.Failure,
                    LiveModels: null);
            }

            await EmitProgressAsync(context, input, step, StatusSucceeded, code: null);

            if (step == UnitValidationStep.ResolvingModel)
            {
                liveModels = ExtractLiveModels(probeOutput.Extras);
            }
        }

        return new UnitValidationWorkflowOutput(
            Success: true,
            Failure: null,
            LiveModels: liveModels);
    }

    private static Task EmitProgressAsync(
        WorkflowContext context,
        UnitValidationWorkflowInput input,
        UnitValidationStep step,
        string status,
        string? code) =>
        context.CallActivityAsync<bool>(
            nameof(EmitValidationProgressActivity),
            new EmitValidationProgressActivityInput(
                UnitId: input.UnitId,
                Step: step,
                Status: status,
                Code: code));

    private static IReadOnlyList<string>? ExtractLiveModels(
        IReadOnlyDictionary<string, string>? extras)
    {
        if (extras is null || !extras.TryGetValue("models", out var csv))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(csv))
        {
            return Array.Empty<string>();
        }

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}