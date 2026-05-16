// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Units;

/// <summary>
/// Input for <see cref="CompleteArtefactValidationActivity"/> — the terminal
/// activity the <see cref="ArtefactValidationWorkflow"/> appends to both the
/// success and failure exit paths. Carries the unit's Dapr actor id
/// (needed for the <c>IUnitActor</c> proxy lookup) together with the
/// workflow's terminal outcome.
/// </summary>
/// <param name="UnitId">The Dapr actor id of the unit whose validation run finished — used to build the <c>IUnitActor</c> proxy inside the activity.</param>
/// <param name="Success"><c>true</c> when every probe step succeeded; <c>false</c> when any step failed.</param>
/// <param name="Failure">Structured failure payload — non-<c>null</c> iff <paramref name="Success"/> is <c>false</c>.</param>
/// <param name="WorkflowInstanceId">The workflow instance id, flowed through so the actor's stale-run guard can compare it against <c>LastValidationRunId</c>.</param>
public record CompleteArtefactValidationActivityInput(
    string UnitId,
    bool Success,
    ArtefactValidationError? Failure,
    string WorkflowInstanceId);
