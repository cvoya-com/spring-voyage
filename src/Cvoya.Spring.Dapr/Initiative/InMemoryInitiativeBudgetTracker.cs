// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Initiative;

/// <summary>
/// Process-local, in-memory <see cref="IInitiativeBudgetTracker"/> implementation that
/// tracks Tier 2 spend per agent and resets the running total on each UTC day
/// rollover. Retained as a test-friendly default; production hosts register
/// <see cref="DaprStateInitiativeBudgetTracker"/> so budget state survives process
/// restarts and is shared across replicas.
/// </summary>
public class InMemoryInitiativeBudgetTracker : IInitiativeBudgetTracker
{
    /// <summary>
    /// Fallback daily cap (in dollars) applied when the agent's policy does not specify
    /// a <see cref="Tier2Config.MaxCostPerDay"/>.
    /// </summary>
    public const decimal DefaultMaxCostPerDay = 3.00m;

    private readonly ConcurrentDictionary<string, AgentBudgetState> _state = new();
    private readonly IAgentPolicyStore _policyStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryInitiativeBudgetTracker"/> class.
    /// </summary>
    /// <param name="policyStore">Policy store used to resolve the per-agent Tier 2 daily cap.</param>
    public InMemoryInitiativeBudgetTracker(IAgentPolicyStore policyStore)
        : this(policyStore, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit <see cref="TimeProvider"/>, used by tests
    /// to advance the clock across UTC-day boundaries.
    /// </summary>
    /// <param name="policyStore">Policy store used to resolve the per-agent Tier 2 daily cap.</param>
    /// <param name="timeProvider">Time source; defaults to <see cref="TimeProvider.System"/>.</param>
    public InMemoryInitiativeBudgetTracker(IAgentPolicyStore policyStore, TimeProvider timeProvider)
    {
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

        var state = _state.GetOrAdd(agentId, _ => new AgentBudgetState(_timeProvider.GetUtcNow().UtcDateTime.Date));
        var today = _timeProvider.GetUtcNow().UtcDateTime.Date;

        lock (state.Gate)
        {
            if (state.Day != today)
            {
                state.Day = today;
                state.Spent = 0m;
            }

            if (state.Spent + estimatedCost > cap)
            {
                return false;
            }

            state.Spent += estimatedCost;
            return true;
        }
    }

    private sealed class AgentBudgetState(DateTime initialDay)
    {
        public readonly object Gate = new();
        public DateTime Day = initialDay;
        public decimal Spent;
    }
}
