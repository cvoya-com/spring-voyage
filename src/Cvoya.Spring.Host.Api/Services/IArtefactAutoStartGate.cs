// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Shared auto-start gate for freshly created artefacts (#2374).
/// Evaluates the same preconditions for both <see cref="ArtefactKind.Unit"/>
/// and <see cref="ArtefactKind.Agent"/> — image + runtime + model on the
/// persisted execution defaults, plus a resolvable LLM credential when the
/// runtime catalogue's first edge declares one. When satisfied, drives the
/// actor into <see cref="LifecycleStatus.Validating"/> and arms the
/// post-validation auto-start marker so the chain reaches
/// <see cref="LifecycleStatus.Running"/> without operator intervention.
/// </summary>
/// <remarks>
/// <para>
/// Previously the unit-side gate lived inline on
/// <c>UnitCreationService.TryAutoStartValidationAsync</c> and the agent-side
/// gate was a near-identical copy on
/// <c>DefaultPackageArtefactActivator.TryAutoStartAgentAsync</c> (#2364 /
/// PR #2371). #2374 extracts the shared logic so the direct-create agent
/// endpoint (<c>AgentEndpoints.CreateAgentAsync</c>) can also call it
/// without duplicating the per-kind branching.
/// </para>
/// <para>
/// Per-kind routing:
/// <list type="bullet">
///   <item>Unit  → reads <see cref="Cvoya.Spring.Core.Execution.IUnitExecutionStore"/>;  credential resolver gets <c>unitId=actorGuid</c>.</item>
///   <item>Agent → reads <see cref="Cvoya.Spring.Core.Execution.IAgentExecutionStore"/>; credential resolver gets <c>agentId=actorGuid</c>.</item>
/// </list>
/// </para>
/// </remarks>
public interface IArtefactAutoStartGate
{
    /// <summary>
    /// Evaluates the gate and, when satisfied, transitions the actor to
    /// <see cref="LifecycleStatus.Validating"/> + sets the
    /// <c>PendingAutoStart</c> marker. Best-effort: any failure leaves the
    /// artefact in <see cref="LifecycleStatus.Draft"/>; the operator can
    /// recover via <c>POST /api/v1/tenant/{units|agents}/{id}/revalidate</c>.
    /// </summary>
    /// <param name="kind">Whether the artefact is a Unit or an Agent.</param>
    /// <param name="actorGuid">The artefact's Dapr actor id (Guid).</param>
    /// <param name="displayName">User-facing name — used only for log correlation.</param>
    /// <param name="cancellationToken">Cancels the gate.</param>
    /// <returns>
    /// <see cref="LifecycleStatus.Validating"/> when the gate transitioned the
    /// actor; <see cref="LifecycleStatus.Draft"/> otherwise.
    /// </returns>
    Task<LifecycleStatus> TryAutoStartAsync(
        ArtefactKind kind,
        Guid actorGuid,
        string displayName,
        CancellationToken cancellationToken = default);
}
