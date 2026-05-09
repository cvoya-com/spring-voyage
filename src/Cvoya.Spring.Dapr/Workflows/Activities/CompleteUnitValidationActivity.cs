// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Terminal activity the <see cref="UnitValidationWorkflow"/> appends to
/// both success and failure exit paths. Builds an <see cref="IUnitActor"/>
/// proxy for the unit under validation and invokes
/// <see cref="IUnitActor.CompleteValidationAsync"/> so the actor can drive
/// the <see cref="UnitStatus.Validating"/> → <see cref="UnitStatus.Stopped"/>
/// (success) or <see cref="UnitStatus.Validating"/> → <see cref="UnitStatus.Error"/>
/// (failure) transition, persist the redacted failure payload, and emit
/// the <c>StateChanged</c> activity event the UI already consumes.
/// </summary>
/// <remarks>
/// <para>
/// The workflow body is deterministic and service-free; the side-effectful
/// actor round-trip has to live inside an activity. The activity returns
/// <c>true</c> when the callback completed (regardless of whether the
/// transition was applied or suppressed by the actor's stale-run /
/// terminal-status guards) and <c>false</c> on a transport-level failure —
/// workflow behaviour is fire-and-forget, so the return value is
/// informational only.
/// </para>
/// </remarks>
public class CompleteUnitValidationActivity(
    IActorProxyFactory actorProxyFactory,
    ILoggerFactory loggerFactory)
    : WorkflowActivity<CompleteUnitValidationActivityInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CompleteUnitValidationActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(
        WorkflowActivityContext context, CompleteUnitValidationActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(input.UnitId), nameof(UnitActor));

            var completion = new UnitValidationCompletion(
                Success: input.Success,
                Failure: input.Failure,
                WorkflowInstanceId: input.WorkflowInstanceId);

            var result = await proxy.CompleteValidationAsync(completion);

            _logger.LogInformation(
                "UnitValidationWorkflow {InstanceId} posted completion to unit {UnitId}. " +
                "Applied={Applied}, CurrentStatus={Status}, Reason={Reason}.",
                input.WorkflowInstanceId, input.UnitId,
                result.Success, result.CurrentStatus, result.RejectionReason ?? "<none>");

            return true;
        }
        catch (Exception ex)
        {
            // Never let a callback failure derail the workflow. The
            // unit's transition will have to be recovered manually
            // (e.g. via /revalidate) if this path fails, but we cannot
            // mask the workflow's own outcome by throwing here.
            _logger.LogError(
                ex,
                "UnitValidationWorkflow {InstanceId} failed to post completion to unit {UnitId}.",
                input.WorkflowInstanceId, input.UnitId);
            return false;
        }
    }
}
