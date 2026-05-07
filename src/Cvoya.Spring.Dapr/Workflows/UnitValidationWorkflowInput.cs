// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

/// <summary>
/// Input for the <see cref="UnitValidationWorkflow"/> describing which unit
/// to validate, the image to pull, the runtime plugin that owns the probe
/// contract, and the credential + target model to exercise. All fields are
/// JSON-serializable so the record can round-trip through the Dapr
/// Workflow engine.
/// </summary>
/// <param name="UnitId">
/// Dapr actor id of the unit being validated. Used for log correlation and
/// as the callback target when <c>CompleteUnitValidationActivity</c> invokes
/// the terminal <c>IUnitActor.CompleteValidationAsync</c> callback.
/// </param>
/// <param name="UnitName">
/// User-facing name of the unit (<c>Address.Path</c> equivalent). Used as
/// the <see cref="Cvoya.Spring.Core.Messaging.Address.Path"/> of progress
/// events (scheme <c>unit</c>) so the web detail page's SSE filter — which
/// keys on the unit's user-facing name, not its actor Guid — picks them up.
/// </param>
/// <param name="Image">
/// Fully-qualified container image reference (e.g. <c>ghcr.io/cvoya/claude:1.2.3</c>)
/// that <c>PullImageActivity</c> will pull and each
/// <c>RunContainerProbeActivity</c> will run the step command inside.
/// </param>
/// <param name="RuntimeId">
/// Stable id of the <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime"/>
/// whose probe steps the workflow should execute. Resolved inside activities
/// via <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntimeRegistry.Get(string)"/>
/// — the workflow body stays delegate-free.
/// </param>
/// <param name="Credential">
/// Raw credential value to inject into the probe environment and use as the
/// redaction key. Empty string when the runtime declares
/// <see cref="Cvoya.Spring.Core.AgentRuntimes.AgentRuntimeCredentialKind.None"/> —
/// <see cref="Cvoya.Spring.Core.Security.CredentialRedactor"/> short-circuits
/// on empty input so no destructive redaction runs.
/// </param>
/// <param name="RequestedModel">
/// Model id the unit's binding will target. Flows into the
/// <see cref="Cvoya.Spring.Core.Units.UnitValidationStep.ResolvingModel"/>
/// probe so the runtime's interpreter can classify 404s as
/// <see cref="Cvoya.Spring.Core.Units.UnitValidationCodes.ModelNotFound"/>.
/// </param>
public record UnitValidationWorkflowInput(
    string UnitId,
    string UnitName,
    string Image,
    string RuntimeId,
    string Credential,
    string RequestedModel);