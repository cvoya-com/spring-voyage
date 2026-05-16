// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Units;

/// <summary>
/// Input to the <c>RunContainerProbeActivity</c> that the
/// <c>ArtefactValidationWorkflow</c> (T-04) invokes to run one probe step inside
/// an already-pulled container image.
/// </summary>
/// <remarks>
/// <para>
/// T-04 refined the T-03 contract: the workflow body must stay deterministic
/// and serializable, but <see cref="Cvoya.Spring.Core.ModelProviders.ProbeStep.InterpretOutput"/>
/// is a <see cref="System.Func{T1, T2, T3, TResult}"/> delegate (not
/// serializable) and the runtime catalogue + launcher registry are DI
/// services (not available in the workflow body). Moving both the
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
/// <see cref="Cvoya.Spring.Core.ModelProviders.ProbeStep.InterpretOutput"/>
/// so the interpreter never sees the raw credential — and also redact any
/// returned <c>Message</c> / <c>Details</c> values a second time as belt-and-braces.
/// </para>
/// </remarks>
/// <param name="RuntimeId">Stable id of the catalogue agent runtime whose probe step this is; resolved via <see cref="Cvoya.Spring.Core.Catalog.IRuntimeCatalog.GetAgentRuntime"/>.</param>
/// <param name="Step">Which step to run from the launcher's <see cref="Cvoya.Spring.Core.Execution.IAgentRuntimeLauncher.GetProbeSteps(Cvoya.Spring.Core.ModelProviders.ModelProviderInstallConfig, string)"/> list — one of <see cref="ArtefactValidationStep.VerifyingTool"/>, <see cref="ArtefactValidationStep.ValidatingCredential"/>, or <see cref="ArtefactValidationStep.ResolvingModel"/>.</param>
/// <param name="Image">The container image reference; the image MUST have been pulled by <c>PullImageActivity</c> first.</param>
/// <param name="Credential">The raw credential to inject into the probe environment and use as the redaction key. Empty when the provider requires no credential — <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/> short-circuits on empty input.</param>
/// <param name="RequestedModel">The model id the unit's install targets; used by the launcher to build the <see cref="ArtefactValidationStep.ResolvingModel"/> probe and by its interpreter to classify 404s as <see cref="ArtefactValidationCodes.ModelNotFound"/>.</param>
public record RunContainerProbeActivityInput(
    string RuntimeId,
    ArtefactValidationStep Step,
    string Image,
    string Credential,
    string RequestedModel);
