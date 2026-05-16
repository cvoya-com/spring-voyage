// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core.Lifecycle;
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
///   <item>Call <see cref="PullImageActivity"/>. Fail short-circuits with <see cref="ArtefactValidationCodes.ImagePullFailed"/> / <see cref="ArtefactValidationCodes.ProbeTimeout"/>.</item>
///   <item>For each <see cref="ArtefactValidationStep"/> in <c>VerifyingTool, ValidatingCredential, ResolvingModel</c>: emit <c>Running</c>, call <see cref="RunContainerProbeActivity"/>, on failure emit <c>Failed</c> + code and return, on success emit <c>Succeeded</c>.</item>
///   <item>After <see cref="ArtefactValidationStep.ResolvingModel"/> succeeds, extract any <c>"models"</c> extras key as the live model catalog.</item>
/// </list>
/// </para>
/// <para>
/// <b>Skipped steps.</b> Providers whose credential schema is
/// <see cref="Cvoya.Spring.Core.ModelProviders.ModelProviderCredentialKind.None"/>
/// (Ollama) omit <see cref="ArtefactValidationStep.ValidatingCredential"/> from
/// their step list; the activity layer returns a "no step declared" error
/// for a request that references a skipped step, so the workflow
/// pre-filters by driving the fixed ordinal sequence and asks the activity
/// only for steps it knows the launcher declared. T-04 hard-codes the
/// universal ordering; T-05+ may revisit.
/// </para>
/// </remarks>
public class ArtefactValidationWorkflow : Workflow<ArtefactValidationWorkflowInput, ArtefactValidationWorkflowOutput>
{
    private const string StatusRunning = "Running";
    private const string StatusSucceeded = "Succeeded";
    private const string StatusFailed = "Failed";

    private static readonly TimeSpan ImagePullTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ordered probe-step sequence the workflow walks after the image pull.
    /// Matches the contract in
    /// <see cref="Cvoya.Spring.Core.Execution.IAgentRuntimeLauncher.GetProbeSteps(Cvoya.Spring.Core.ModelProviders.ModelProviderInstallConfig, string)"/>:
    /// <see cref="ArtefactValidationStep.PullingImage"/> is the workflow's own
    /// first step and is not included here.
    /// </summary>
    internal static readonly ArtefactValidationStep[] PostPullSteps =
    {
        ArtefactValidationStep.VerifyingTool,
        ArtefactValidationStep.ValidatingCredential,
        ArtefactValidationStep.ResolvingModel,
    };

    /// <inheritdoc />
    public override async Task<ArtefactValidationWorkflowOutput> RunAsync(
        WorkflowContext context, ArtefactValidationWorkflowInput input)
    {
        // Step 1: Pull the image.
        await EmitProgressAsync(context, input, ArtefactValidationStep.PullingImage, StatusRunning, code: null);

        var pullOutput = await context.CallActivityAsync<PullImageActivityOutput>(
            nameof(PullImageActivity),
            new PullImageActivityInput(input.Image, ImagePullTimeout));

        if (!pullOutput.Success)
        {
            await EmitProgressAsync(
                context,
                input,
                ArtefactValidationStep.PullingImage,
                StatusFailed,
                pullOutput.Failure?.Code);

            var pullFailure = new ArtefactValidationWorkflowOutput(
                Success: false,
                Failure: pullOutput.Failure,
                LiveModels: null);
            await PostCompletionAsync(context, input, pullFailure);
            return pullFailure;
        }

        await EmitProgressAsync(
            context, input, ArtefactValidationStep.PullingImage, StatusSucceeded, code: null);

        // Steps 2..N: walk each post-pull probe step in order,
        // skipping any step the runtime did not declare (no events emitted
        // → UI shows those steps as "skipped").
        var skipSet = input.SkipSteps is { Length: > 0 }
            ? new HashSet<string>(input.SkipSteps, StringComparer.OrdinalIgnoreCase)
            : null;

        IReadOnlyList<string>? liveModels = null;

        foreach (var step in PostPullSteps)
        {
            if (skipSet is not null && skipSet.Contains(step.ToString()))
            {
                continue;
            }

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

                var probeFailure = new ArtefactValidationWorkflowOutput(
                    Success: false,
                    Failure: probeOutput.Failure,
                    LiveModels: null);
                await PostCompletionAsync(context, input, probeFailure);
                return probeFailure;
            }

            await EmitProgressAsync(context, input, step, StatusSucceeded, code: null);

            if (step == ArtefactValidationStep.ResolvingModel)
            {
                liveModels = ExtractLiveModels(probeOutput.Extras);
            }
        }

        var success = new ArtefactValidationWorkflowOutput(
            Success: true,
            Failure: null,
            LiveModels: liveModels);
        await PostCompletionAsync(context, input, success);
        return success;
    }

    /// <summary>
    /// Posts the workflow's terminal outcome to the unit actor via
    /// <see cref="CompleteArtefactValidationActivity"/> so the actor can drive
    /// the <see cref="LifecycleStatus.Validating"/> → <see cref="LifecycleStatus.Stopped"/>
    /// or <see cref="LifecycleStatus.Validating"/> → <see cref="LifecycleStatus.Error"/>
    /// transition and persist the redacted failure payload. The activity
    /// is best-effort — a failure to notify the actor is logged and
    /// swallowed so the workflow's own outcome is never masked.
    /// </summary>
    private static Task PostCompletionAsync(
        WorkflowContext context,
        ArtefactValidationWorkflowInput input,
        ArtefactValidationWorkflowOutput output) =>
        context.CallActivityAsync<bool>(
            nameof(CompleteArtefactValidationActivity),
            new CompleteArtefactValidationActivityInput(
                Kind: input.Kind,
                ArtefactId: input.ArtefactId,
                Success: output.Success,
                Failure: output.Failure,
                WorkflowInstanceId: context.InstanceId));

    private static Task EmitProgressAsync(
        WorkflowContext context,
        ArtefactValidationWorkflowInput input,
        ArtefactValidationStep step,
        string status,
        string? code) =>
        context.CallActivityAsync<bool>(
            nameof(EmitValidationProgressActivity),
            new EmitValidationProgressActivityInput(
                Kind: input.Kind,
                ArtefactId: Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(input.ArtefactId, out var artefactGuid)
                    ? artefactGuid
                    : Guid.Empty,
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
