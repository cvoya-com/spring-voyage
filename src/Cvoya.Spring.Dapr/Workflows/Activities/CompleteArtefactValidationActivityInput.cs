// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Input for <see cref="CompleteArtefactValidationActivity"/> — the terminal
/// activity the <see cref="ArtefactValidationWorkflow"/> appends to both the
/// success and failure exit paths. Carries the artefact's kind + Dapr actor
/// id (needed for the right actor proxy lookup) together with the workflow's
/// terminal outcome.
/// </summary>
/// <param name="Kind">Whether the artefact whose validation finished is a Unit or an Agent — routes the terminal callback to the right actor type.</param>
/// <param name="ArtefactId">The Dapr actor id of the artefact — used to build the actor proxy inside the activity.</param>
/// <param name="Success"><c>true</c> when every probe step succeeded; <c>false</c> when any step failed.</param>
/// <param name="Failure">Structured failure payload — non-<c>null</c> iff <paramref name="Success"/> is <c>false</c>.</param>
/// <param name="WorkflowInstanceId">The workflow instance id, flowed through so the actor's stale-run guard can compare it against <c>LastValidationRunId</c>.</param>
public record CompleteArtefactValidationActivityInput(
    ArtefactKind Kind,
    string ArtefactId,
    bool Success,
    ArtefactValidationError? Failure,
    string WorkflowInstanceId);
