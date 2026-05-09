// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Initiative;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentLifecycleCoordinator"/>.
/// Owns the actor-activation / expertise-seeding concern extracted from
/// <c>AgentActor</c>: applying expertise declared in <c>AgentDefinition</c>
/// YAML to actor state on first activation (#488).
/// </summary>
/// <remarks>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected
/// singletons. This makes it safe to register as a singleton and share
/// across all <c>AgentActor</c> instances.
/// </para>
/// <para>
/// Failures in seeding are non-fatal: the coordinator logs a warning and
/// returns without throwing so the actor still activates with empty expertise.
/// The operator can push the seed later via
/// <c>PUT /api/v1/agents/{id}/expertise</c>.
/// </para>
/// </remarks>
public class AgentLifecycleCoordinator(
    ILogger<AgentLifecycleCoordinator> logger) : IAgentLifecycleCoordinator
{
    /// <inheritdoc />
    public async Task ActivateAsync(
        string agentId,
        Func<CancellationToken, Task<(bool hasValue, List<ExpertiseDomain>? value)>> getExistingExpertise,
        Func<CancellationToken, Task<IReadOnlyList<ExpertiseDomain>?>> getSeed,
        Func<ExpertiseDomain[], CancellationToken, Task> persistExpertise,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (hasValue, _) = await getExistingExpertise(cancellationToken);

            // Actor state wins — if ANY value (including an empty list) was
            // persisted through SetExpertiseAsync, the operator's runtime
            // edit is preserved across activations.
            if (hasValue)
            {
                return;
            }

            var seed = await getSeed(cancellationToken);
            if (seed is null || seed.Count == 0)
            {
                return;
            }

            await persistExpertise(seed.ToArray(), cancellationToken);

            logger.LogInformation(
                "Agent {AgentId} seeded expertise from AgentDefinition YAML. Domain count: {Count}",
                agentId, seed.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Agent {AgentId} failed to seed expertise from AgentDefinition; activation proceeding with empty expertise.",
                agentId);
        }
    }
}
