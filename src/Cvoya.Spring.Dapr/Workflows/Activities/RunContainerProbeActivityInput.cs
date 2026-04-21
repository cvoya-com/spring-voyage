// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

/// <summary>
/// Input to the <c>RunContainerProbeActivity</c> that the
/// <c>UnitValidationWorkflow</c> (T-04) invokes to exec one probe command
/// inside an already-pulled container image. This record carries only
/// value-typed, JSON-serializable fields so it crosses the Dapr Workflow
/// boundary cleanly; the interpreter delegate from
/// <see cref="Cvoya.Spring.Core.AgentRuntimes.ProbeStep.InterpretOutput"/>
/// stays on the workflow side and runs against the activity's output
/// triple.
/// </summary>
/// <remarks>
/// The activity MUST pass the produced <c>stdout</c> and <c>stderr</c>
/// through <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/>
/// keyed on <paramref name="CredentialForRedaction"/> BEFORE returning the
/// <c>RunContainerProbeActivityOutput</c>, so the raw credential never
/// reaches workflow state, persisted unit-validation errors, or logs.
/// </remarks>
/// <param name="Image">The container image reference; the image MUST have been pulled by <c>PullImageActivity</c> first.</param>
/// <param name="Args">argv-style command + arguments to run inside the container (index 0 is the executable).</param>
/// <param name="Env">Environment variables to set on the container process, including any credential the probe requires.</param>
/// <param name="Timeout">Maximum wall-clock time the activity allows the command to run before terminating it and surfacing <see cref="Cvoya.Spring.Core.Units.UnitValidationCodes.ProbeTimeout"/>.</param>
/// <param name="CredentialForRedaction">
/// The raw credential value the workflow passed through <paramref name="Env"/>,
/// used by the activity to redact stdout / stderr before returning them.
/// Empty string when the probe needs no credential — the redactor
/// short-circuits on empty input.
/// </param>
public record RunContainerProbeActivityInput(
    string Image,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    TimeSpan Timeout,
    string CredentialForRedaction);