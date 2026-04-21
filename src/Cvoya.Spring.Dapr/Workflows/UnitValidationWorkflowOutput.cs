// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Output of the <see cref="UnitValidationWorkflow"/>: a boolean verdict,
/// the originating <see cref="UnitValidationError"/> on failure, and an
/// optional parsed live-model catalog on success.
/// </summary>
/// <remarks>
/// T-05 will consume this record inside <c>UnitActor</c> to decide the
/// next lifecycle transition — <c>Success == true</c> triggers
/// <see cref="UnitStatus.Stopped"/>, otherwise <see cref="UnitStatus.Error"/>
/// with <see cref="Failure"/> persisted as <c>LastValidationErrorJson</c>.
/// </remarks>
/// <param name="Success"><c>true</c> when every probe step passed; <c>false</c> when any step failed.</param>
/// <param name="Failure">Structured failure payload — non-<c>null</c> iff <see cref="Success"/> is <c>false</c>. Carries the step that failed, a stable <see cref="UnitValidationCodes"/> code, an operator message, and optional structured details.</param>
/// <param name="LiveModels">Live model catalog parsed from the <see cref="UnitValidationStep.ResolvingModel"/> step's <see cref="Cvoya.Spring.Core.AgentRuntimes.StepResult.Extras"/> (key <c>"models"</c>, comma-separated) when the runtime's interpreter emitted one; <c>null</c> on failure or when the runtime does not enumerate a live catalog (e.g. the Claude CLI probe only confirms the requested model).</param>
public record UnitValidationWorkflowOutput(
    bool Success,
    UnitValidationError? Failure,
    IReadOnlyList<string>? LiveModels);