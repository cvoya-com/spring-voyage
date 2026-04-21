// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

using Cvoya.Spring.Core.Units;

/// <summary>
/// One declarative probe command the host will execute inside the unit's
/// chosen container image as part of unit validation. Populated by
/// <see cref="IAgentRuntime.GetProbeSteps(AgentRuntimeInstallConfig, string)"/>.
/// </summary>
/// <remarks>
/// <para>
/// T-03 splits the probe into two halves: the runtime builds the declarative
/// command (<see cref="Args"/>, <see cref="Env"/>, <see cref="Timeout"/>) and
/// its interpreter (<see cref="InterpretOutput"/>); the host (the
/// <c>UnitValidationWorkflow</c> Dapr workflow + its activities) is
/// responsible for pulling the image, running the command inside the
/// container, enforcing the timeout, capturing stdout/stderr, redacting the
/// credential value via <see cref="Security.CredentialRedactor"/>, and
/// feeding the triple back through <see cref="InterpretOutput"/>.
/// </para>
/// <para>
/// <b><see cref="UnitValidationStep.PullingImage"/> is NEVER returned by
/// <see cref="IAgentRuntime.GetProbeSteps(AgentRuntimeInstallConfig, string)"/>.</b>
/// The image pull is a runtime-agnostic concern owned by the dispatcher /
/// workflow: it is performed once up-front for every runtime, so the
/// runtime contract starts at <see cref="UnitValidationStep.VerifyingTool"/>.
/// </para>
/// <para>
/// Because <see cref="InterpretOutput"/> is a <see cref="Func{T1, T2, T3, TResult}"/>
/// delegate, a <see cref="ProbeStep"/> is NOT serializable across the Dapr
/// Workflow boundary. The workflow keeps the step in-process (it has the
/// runtime instance) and only ships the serializable command payload
/// (<see cref="Args"/>, <see cref="Env"/>, <see cref="Timeout"/>) to the
/// container-exec activity; the activity returns the raw
/// <c>(exitCode, stdout, stderr)</c> triple and the workflow invokes
/// <see cref="InterpretOutput"/> on its side.
/// </para>
/// </remarks>
/// <param name="Step">
/// The unit-validation step this probe exercises. Must be one of
/// <see cref="UnitValidationStep.VerifyingTool"/>,
/// <see cref="UnitValidationStep.ValidatingCredential"/>, or
/// <see cref="UnitValidationStep.ResolvingModel"/>.
/// <see cref="UnitValidationStep.PullingImage"/> is reserved for the
/// dispatcher and must not appear.
/// </param>
/// <param name="Args">
/// argv-style command + arguments to run inside the container. Index 0 is
/// the executable name (resolved on the container's <c>PATH</c>).
/// </param>
/// <param name="Env">
/// Environment variables to set on the container process. Credentials
/// required by the probe are passed here (e.g. <c>ANTHROPIC_API_KEY</c>)
/// and MUST NOT be embedded in <see cref="Args"/> — the host logs the argv
/// but never the environment map.
/// </param>
/// <param name="Timeout">
/// Maximum wall-clock time the host will allow the container command to run
/// before it is terminated and the step is reported as
/// <see cref="UnitValidationCodes.ProbeTimeout"/>.
/// </param>
/// <param name="InterpretOutput">
/// Interprets the container's <c>(exitCode, stdout, stderr)</c> triple and
/// returns a <see cref="StepResult"/>. The delegate is invoked by the
/// workflow AFTER the activity has redacted the credential from the stdout
/// and stderr buffers; implementers should treat the strings as
/// already-redacted and parse them for success/failure signals only (HTTP
/// status codes, JSON envelopes, etc.). The delegate must not throw: return
/// a failure with code <see cref="UnitValidationCodes.ProbeInternalError"/>
/// for any unexpected shape.
/// </param>
public sealed record ProbeStep(
    UnitValidationStep Step,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    TimeSpan Timeout,
    Func<int, string, string, StepResult> InterpretOutput);