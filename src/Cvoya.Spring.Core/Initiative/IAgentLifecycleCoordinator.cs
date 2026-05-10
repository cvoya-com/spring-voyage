// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Seam that encapsulates the actor-activation / expertise-seeding concern
/// extracted from <c>AgentActor</c>: checking whether actor state already
/// holds an expertise list and, when it does not, pulling a declarative seed
/// from <c>AgentDefinition</c> YAML via <see cref="IExpertiseSeedProvider"/>
/// and applying it through the actor's <c>SetExpertiseAsync</c> path.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that layers audit logging
/// on every seeding event or gates seeding on per-tenant flags) without
/// touching the actor. Per the platform's "interface-first + TryAdd*" rule,
/// production DI registers the default implementation with
/// <c>TryAddSingleton</c> so the private repo's registration takes precedence
/// when present.
/// </para>
/// <para>
/// The coordinator does not hold a reference to the actor. Instead,
/// <see cref="ActivateAsync"/> receives delegate parameters so the actor can
/// inject its own state-read, state-write, and seed-fetch implementations
/// without the coordinator depending on Dapr actor types or scoped DI
/// services.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected
/// singleton seams. This makes it safe to register as a singleton and share
/// across all <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentLifecycleCoordinator
{
    /// <summary>
    /// Runs the actor-activation logic for a single agent: checks whether
    /// actor state already holds an expertise list and, when it does not,
    /// fetches the declarative seed from
    /// <see cref="IExpertiseSeedProvider.GetAgentSeedAsync"/> and applies it
    /// through <paramref name="persistExpertise"/>. Called by the actor's
    /// <c>OnActivateAsync</c> template method as a thin shim.
    /// </summary>
    /// <param name="agentId">
    /// The Dapr actor id (<c>Id.GetId()</c>) of the activating agent. Passed
    /// to the seed provider and used for log correlation.
    /// </param>
    /// <param name="hasExistingExpertise">
    /// Delegate that returns <c>true</c> when the agent already has an
    /// explicit expertise list persisted (even an empty list). The
    /// coordinator uses this to honour the actor-state-wins precedence
    /// rule (#488) — once an explicit value has been written through
    /// <c>SetExpertiseAsync</c>, the YAML seed is not re-applied. Passed
    /// as a delegate so the coordinator can remain a singleton even
    /// though the underlying read goes through the EF
    /// <c>agent_live_config.expertise_initialised</c> flag (ADR-0040 /
    /// #2048).
    /// </param>
    /// <param name="getSeed">
    /// Delegate that fetches the declarative expertise seed for the agent.
    /// Returns <c>null</c> when no seed is declared; an empty list when the
    /// <c>expertise:</c> block is present but empty. Passed as a delegate so
    /// the coordinator can remain a singleton even though
    /// <see cref="IExpertiseSeedProvider"/> may be absent (the actor treats
    /// it as optional).
    /// </param>
    /// <param name="persistExpertise">
    /// Delegate that writes the seeded expertise to actor state and emits the
    /// corresponding activity event. The actor's own <c>SetExpertiseAsync</c>
    /// is the canonical caller; the coordinator does not write state directly.
    /// </param>
    /// <param name="cancellationToken">Cancels the activation operation.</param>
    Task ActivateAsync(
        string agentId,
        Func<CancellationToken, Task<bool>> hasExistingExpertise,
        Func<CancellationToken, Task<IReadOnlyList<ExpertiseDomain>?>> getSeed,
        Func<ExpertiseDomain[], CancellationToken, Task> persistExpertise,
        CancellationToken cancellationToken = default);
}
