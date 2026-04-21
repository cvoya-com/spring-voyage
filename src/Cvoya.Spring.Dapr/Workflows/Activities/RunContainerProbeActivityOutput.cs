// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Structured outcome returned by <c>RunContainerProbeActivity</c>: either a
/// success verdict (optionally with <see cref="Extras"/> forwarded from the
/// runtime's interpreter) OR a failure with a stable code from
/// <see cref="UnitValidationCodes"/>. Every string field MUST have been
/// passed through <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/>
/// before this record leaves the activity process.
/// </summary>
/// <remarks>
/// <para>
/// T-04 refined the T-03 shape from the raw <c>(exitCode, stdout, stderr)</c>
/// triple to this pre-interpreted payload. The reason: the workflow body
/// must be deterministic + delegate-free, so interpretation happens in the
/// activity (where DI + runtime registry live) rather than in the workflow.
/// See <see cref="RunContainerProbeActivityInput"/> for the rationale in
/// full.
/// </para>
/// <para>
/// <see cref="RedactedStdOut"/> and <see cref="RedactedStdErr"/> are always
/// populated so the workflow (and any diagnostic hook) can log or persist
/// them without risk of leaking the credential. Logs should never need to
/// re-run the redactor.
/// </para>
/// </remarks>
/// <param name="Success"><c>true</c> when the step's <see cref="Cvoya.Spring.Core.AgentRuntimes.ProbeStep.InterpretOutput"/> returned <see cref="Cvoya.Spring.Core.AgentRuntimes.StepOutcome.Succeeded"/>; <c>false</c> on any failure path (container run failure, timeout, interpreter failure, or internal error).</param>
/// <param name="Failure">Structured failure payload — <c>null</c> when <see cref="Success"/> is <c>true</c>; otherwise carries the originating <see cref="UnitValidationStep"/>, a stable <see cref="UnitValidationCodes"/> code, a redacted operator message, and optional structured details.</param>
/// <param name="Extras">Optional success-path payload from <see cref="Cvoya.Spring.Core.AgentRuntimes.StepResult.Extras"/>. For <see cref="UnitValidationStep.ResolvingModel"/> this carries the comma-separated <c>"models"</c> catalog under the <c>models</c> key; <c>null</c> when the interpreter emits nothing.</param>
/// <param name="RedactedStdOut">The container process' stdout with the credential replaced by <c>***</c>. Always populated.</param>
/// <param name="RedactedStdErr">The container process' stderr with the credential replaced by <c>***</c>. Always populated.</param>
public record RunContainerProbeActivityOutput(
    bool Success,
    UnitValidationError? Failure,
    IReadOnlyDictionary<string, string>? Extras,
    string RedactedStdOut,
    string RedactedStdErr);