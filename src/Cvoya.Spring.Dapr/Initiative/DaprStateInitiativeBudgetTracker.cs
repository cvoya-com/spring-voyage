// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.State;

/// <summary>
/// Dapr state-store-backed <see cref="IInitiativeBudgetTracker"/> that persists the
/// running daily Tier 2 spend for each agent under <c>"Initiative:Budget:{agentId}"</c>.
/// Automatically resets the running total on UTC-day rollover.
/// </summary>
/// <remarks>
/// State persistence ensures budget caps survive process restarts and are observed
/// across replicas. Concurrent <see cref="TryConsumeAsync"/> calls for the same agent
/// are serialised through the underlying state store's last-writer-wins semantics;
/// for strict concurrency guarantees a future iteration could layer optimistic
/// concurrency via the Dapr ETag APIs. See issue #99.
/// </remarks>
public class DaprStateInitiativeBudgetTracker : IInitiativeBudgetTracker
{
    /// <summary>
    /// Fallback daily cap (in dollars) applied when the agent's policy does not specify
    /// a <see cref="Tier2Config.MaxCostPerDay"/>.
    /// </summary>
    public const decimal DefaultMaxCostPerDay = 3.00m;

    private const string BudgetKeyPrefix = "Initiative:Budget:";

    private readonly IStateStore _stateStore;
    private readonly IAgentPolicyStore _policyStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DaprStateInitiativeBudgetTracker"/> class
    /// using the system <see cref="TimeProvider"/>.
    /// </summary>
    /// <param name="stateStore">State store used to persist per-agent budget state.</param>
    /// <param name="policyStore">Policy store used to resolve the per-agent Tier 2 daily cap.</param>
    public DaprStateInitiativeBudgetTracker(IStateStore stateStore, IAgentPolicyStore policyStore)
        : this(stateStore, policyStore, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit <see cref="TimeProvider"/>, used by
    /// tests to advance the clock across UTC-day boundaries.
    /// </summary>
    /// <param name="stateStore">State store used to persist per-agent budget state.</param>
    /// <param name="policyStore">Policy store used to resolve the per-agent Tier 2 daily cap.</param>
    /// <param name="timeProvider">Time source; defaults to <see cref="TimeProvider.System"/>.</param>
    public DaprStateInitiativeBudgetTracker(
        IStateStore stateStore,
        IAgentPolicyStore policyStore,
        TimeProvider timeProvider)
    {
        _stateStore = stateStore;
        _policyStore = policyStore;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<bool> TryConsumeAsync(string agentId, decimal estimatedCost, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        cancellationToken.ThrowIfCancellationRequested();

        var policy = await _policyStore.GetPolicyAsync(agentId, cancellationToken);
        var cap = policy.Tier2?.MaxCostPerDay ?? DefaultMaxCostPerDay;

        var today = _timeProvider.GetUtcNow().UtcDateTime.Date;
        var key = BudgetKeyPrefix + agentId;

        var existing = await _stateStore.GetAsync<AgentBudgetState>(key, cancellationToken);
        var state = existing is null || existing.Day != today
            ? new AgentBudgetState(today, 0m)
            : existing;

        var projected = state.Spent + estimatedCost;
        if (projected > cap)
        {
            // Persist the (possibly rolled-over) zeroed state so the day boundary is
            // recorded even when a call is rejected at the cap.
            if (existing is null || existing.Day != state.Day)
            {
                await _stateStore.SetAsync(key, state, cancellationToken);
            }

            return false;
        }

        var updated = state with { Spent = projected };
        await _stateStore.SetAsync(key, updated, cancellationToken);
        return true;
    }

    /// <summary>
    /// Per-agent budget state persisted to the Dapr state store.
    /// </summary>
    /// <param name="Day">The UTC calendar day whose spend is accumulated.</param>
    /// <param name="Spent">The running Tier 2 spend for <paramref name="Day"/>.</param>
    public sealed record AgentBudgetState(DateTime Day, decimal Spent);
}
