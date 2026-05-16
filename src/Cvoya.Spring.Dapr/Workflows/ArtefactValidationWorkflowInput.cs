// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Input for the <see cref="ArtefactValidationWorkflow"/> describing which
/// artefact (unit or agent — see <see cref="Kind"/>) to validate, the image
/// to pull, the runtime plugin that owns the probe contract, and the
/// credential + target model to exercise. All fields are JSON-serializable
/// so the record can round-trip through the Dapr Workflow engine.
/// </summary>
/// <param name="Kind">
/// Whether the artefact under validation is an <see cref="ArtefactKind.Unit"/>
/// or an <see cref="ArtefactKind.Agent"/>. Drives the terminal callback in
/// <c>CompleteArtefactValidationActivity</c> (routes to <c>IUnitActor</c>
/// vs <c>IAgentActor</c>). <see cref="ArtefactKind.Skill"/> and
/// <see cref="ArtefactKind.Workflow"/> are rejected by the scheduler — they
/// have no container lifecycle.
/// </param>
/// <param name="ArtefactId">
/// Dapr actor id of the artefact being validated. Used for log correlation
/// and as the callback target when <c>CompleteArtefactValidationActivity</c>
/// invokes the terminal <c>CompleteValidationAsync</c> on the matching actor.
/// </param>
/// <param name="ArtefactName">
/// User-facing name of the artefact (<c>Address.Path</c> equivalent). Used as
/// the <see cref="Cvoya.Spring.Core.Messaging.Address.Path"/> of progress
/// events so the web detail page's SSE filter — which keys on the user-facing
/// name, not the actor Guid — picks them up. The address scheme is derived
/// from <see cref="Kind"/> (<c>unit</c> or <c>agent</c>).
/// </param>
/// <param name="Image">
/// Fully-qualified container image reference (e.g. <c>ghcr.io/cvoya/claude:1.2.3</c>)
/// that <c>PullImageActivity</c> will pull and each
/// <c>RunContainerProbeActivity</c> will run the step command inside.
/// </param>
/// <param name="RuntimeId">
/// Stable id of the catalogue
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime"/> whose probe
/// steps the workflow should execute. Resolved inside activities via
/// <see cref="Cvoya.Spring.Core.Catalog.IRuntimeCatalog.GetAgentRuntime"/>
/// — the workflow body stays delegate-free.
/// </param>
/// <param name="Credential">
/// Raw credential value to inject into the probe environment and use as the
/// redaction key. Empty string when the provider declares
/// <see cref="Cvoya.Spring.Core.ModelProviders.ModelProviderCredentialKind.None"/> —
/// <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/> short-circuits
/// on empty input so no destructive redaction runs.
/// </param>
/// <param name="RequestedModel">
/// Model id the artefact's binding will target. Flows into the
/// <see cref="ArtefactValidationStep.ResolvingModel"/> probe so the runtime's
/// interpreter can classify 404s as <see cref="ArtefactValidationCodes.ModelNotFound"/>.
/// </param>
/// <param name="SkipSteps">
/// Probe steps the launcher does not declare and the workflow should skip
/// entirely (emitting no events for them so the UI shows them as "skipped").
/// Computed by <see cref="ArtefactValidationWorkflowScheduler"/> from the
/// launcher's
/// <see cref="Cvoya.Spring.Core.Execution.IAgentRuntimeLauncher.GetProbeSteps"/>
/// result and stored as string names (e.g. <c>"ValidatingCredential"</c>)
/// so the record stays JSON-serializable across the Dapr Workflow boundary.
/// <c>null</c> or empty means all post-pull steps are attempted.
/// </param>
public record ArtefactValidationWorkflowInput(
    ArtefactKind Kind,
    string ArtefactId,
    string ArtefactName,
    string Image,
    string RuntimeId,
    string Credential,
    string RequestedModel,
    string[]? SkipSteps = null);
