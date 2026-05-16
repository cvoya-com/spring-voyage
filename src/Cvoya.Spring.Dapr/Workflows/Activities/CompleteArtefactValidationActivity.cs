// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Terminal activity the <see cref="ArtefactValidationWorkflow"/> appends to
/// both success and failure exit paths. Builds the right actor proxy for the
/// artefact under validation (<see cref="IUnitActor"/> for
/// <see cref="ArtefactKind.Unit"/>, <see cref="IAgentActor"/> for
/// <see cref="ArtefactKind.Agent"/>) and invokes its
/// <c>CompleteValidationAsync</c> so the actor can drive the
/// <see cref="LifecycleStatus.Validating"/> → <see cref="LifecycleStatus.Stopped"/>
/// (success) or <see cref="LifecycleStatus.Validating"/> → <see cref="LifecycleStatus.Error"/>
/// (failure) transition, persist the redacted failure payload, and emit the
/// <c>StateChanged</c> activity event the UI already consumes.
/// </summary>
/// <remarks>
/// The workflow body is deterministic and service-free; the side-effectful
/// actor round-trip has to live inside an activity. The activity returns
/// <c>true</c> when the callback completed (regardless of whether the
/// transition was applied or suppressed by the actor's stale-run /
/// terminal-status guards) and <c>false</c> on a transport-level failure —
/// workflow behaviour is fire-and-forget, so the return value is
/// informational only.
/// </remarks>
public class CompleteArtefactValidationActivity(
    IActorProxyFactory actorProxyFactory,
    ILoggerFactory loggerFactory)
    : WorkflowActivity<CompleteArtefactValidationActivityInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CompleteArtefactValidationActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(
        WorkflowActivityContext context, CompleteArtefactValidationActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var completion = new ArtefactValidationCompletion(
            Success: input.Success,
            Failure: input.Failure,
            WorkflowInstanceId: input.WorkflowInstanceId);

        try
        {
            var result = input.Kind switch
            {
                ArtefactKind.Unit => await actorProxyFactory
                    .CreateActorProxy<IUnitActor>(new ActorId(input.ArtefactId), nameof(UnitActor))
                    .CompleteValidationAsync(completion),
                ArtefactKind.Agent => await actorProxyFactory
                    .CreateActorProxy<IAgentActor>(new ActorId(input.ArtefactId), nameof(AgentActor))
                    .CompleteValidationAsync(completion),
                _ => throw new InvalidOperationException(
                    $"ArtefactKind '{input.Kind}' has no container lifecycle — " +
                    "Skill and Workflow kinds must be rejected by the scheduler before reaching this activity."),
            };

            _logger.LogInformation(
                "ArtefactValidationWorkflow {InstanceId} posted completion to {Kind} {ArtefactId}. " +
                "Applied={Applied}, CurrentStatus={Status}, Reason={Reason}.",
                input.WorkflowInstanceId, input.Kind, input.ArtefactId,
                result.Success, result.CurrentStatus, result.RejectionReason ?? "<none>");

            return true;
        }
        catch (Exception ex)
        {
            // Never let a callback failure derail the workflow. The
            // artefact's transition will have to be recovered manually
            // (e.g. via /revalidate) if this path fails, but we cannot
            // mask the workflow's own outcome by throwing here.
            _logger.LogError(
                ex,
                "ArtefactValidationWorkflow {InstanceId} failed to post completion to {Kind} {ArtefactId}.",
                input.WorkflowInstanceId, input.Kind, input.ArtefactId);
            return false;
        }
    }
}
