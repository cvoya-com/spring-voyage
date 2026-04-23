// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Identifies the probe step a unit-validation run is executing — or the step
/// that failed, when persisted on a <see cref="UnitValidationError"/>. Probes run
/// inside the unit's chosen container image and are orchestrated by a Dapr workflow.
/// The set is intentionally closed: adding a step means extending the workflow.
/// </summary>
public enum UnitValidationStep
{
    /// <summary>
    /// Pulling the unit's container image and verifying it can start. Catches missing
    /// images, auth failures against the container registry, and images that fail to
    /// launch (bad entrypoint, immediate crash).
    /// </summary>
    PullingImage,

    /// <summary>
    /// Verifying that the baseline tooling the runtime declares as required is present
    /// inside the running container (e.g. a CLI binary the runtime invokes). Catches
    /// images that pull and start but are missing runtime prerequisites.
    /// </summary>
    VerifyingTool,

    /// <summary>
    /// Exercising the declared credential against the remote service with a canary
    /// probe request. Distinguishes a credential rejected on format from one rejected
    /// on authentication. Runtimes that do not authenticate skip this step.
    /// </summary>
    ValidatingCredential,

    /// <summary>
    /// Resolving the configured model name against the runtime's catalog or the
    /// authenticated provider. Runtimes that do not consume a model identifier skip
    /// this step.
    /// </summary>
    ResolvingModel,

    /// <summary>
    /// Scheduling the unit-validation workflow itself — the host-side step that
    /// runs in <see cref="UnitActor"/> *before* any in-container probe. Reported
    /// when the actor accepts a transition into <see cref="UnitStatus.Validating"/>
    /// but the call into <c>IUnitValidationWorkflowScheduler.ScheduleAsync</c>
    /// throws (Dapr workflow runtime unavailable, scheduler dependency
    /// unresolved, etc.). The failure is host-side: no probe step ever ran.
    /// </summary>
    SchedulingWorkflow,
}