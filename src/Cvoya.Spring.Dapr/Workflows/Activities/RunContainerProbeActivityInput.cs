// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Input to the <c>RunContainerProbeActivity</c> that the
/// <c>UnitValidationWorkflow</c> (T-04) invokes to run one probe step inside
/// an already-pulled container image.
/// </summary>
/// <remarks>
/// <para>
/// T-04 refined the T-03 contract: the workflow body must stay deterministic
/// and serializable, but <see cref="Cvoya.Spring.Core.AgentRuntimes.ProbeStep.InterpretOutput"/>
/// is a <see cref="System.Func{T1, T2, T3, TResult}"/> delegate (not
/// serializable) and <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntimeRegistry"/>
/// is a DI singleton (not available in the workflow body). Moving both the
/// container exec AND the <c>InterpretOutput</c> call inside this activity
/// keeps the workflow delegate-free and keeps interpreter injection where
/// DI lives. The activity is passed just enough context to resolve the
/// runtime, pick the right step, inject the credential, redact stdout/stderr,
/// and package a structured <see cref="RunContainerProbeActivityOutput"/>.
/// </para>
/// <para>
/// The activity MUST pass produced <c>stdout</c> / <c>stderr</c> through
/// <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/> keyed on
/// <paramref name="Credential"/> BEFORE invoking
/// <see cref="Cvoya.Spring.Core.AgentRuntimes.ProbeStep.InterpretOutput"/>
/// so the interpreter never sees the raw credential — and also redact any
/// returned <c>Message</c> / <c>Details</c> values a second time as belt-and-braces.
/// </para>
/// </remarks>
/// <param name="RuntimeId">Stable id of the agent runtime whose probe step this is; resolved via <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntimeRegistry.Get(string)"/>.</param>
/// <param name="Step">Which step to run from the runtime's <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime.GetProbeSteps(Cvoya.Spring.Core.AgentRuntimes.AgentRuntimeInstallConfig, string)"/> list — one of <see cref="UnitValidationStep.VerifyingTool"/>, <see cref="UnitValidationStep.ValidatingCredential"/>, or <see cref="UnitValidationStep.ResolvingModel"/>.</param>
/// <param name="Image">The container image reference; the image MUST have been pulled by <c>PullImageActivity</c> first.</param>
/// <param name="Credential">The raw credential to inject into the probe environment and use as the redaction key. Empty when the runtime requires no credential — <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/> short-circuits on empty input.</param>
/// <param name="RequestedModel">The model id the unit's install targets; used by the runtime to build the <see cref="UnitValidationStep.ResolvingModel"/> probe and by its interpreter to classify 404s as <see cref="UnitValidationCodes.ModelNotFound"/>.</param>
public record RunContainerProbeActivityInput(
    string RuntimeId,
    UnitValidationStep Step,
    string Image,
    string Credential,
    string RequestedModel);