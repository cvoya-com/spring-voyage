// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Cloning;

/// <summary>
/// In-memory test double for <see cref="IAgentCloningPolicyRepository"/>
/// (#2051 / ADR-0040). Lets unit tests exercise the cloning-policy
/// surface without standing up a Postgres / Testcontainer or even the
/// EF in-memory provider. Cross-restart and tenant-isolation behaviour
/// are covered by <c>CloningPolicyRepositoryTests</c> against the EF
/// surface; this double is for tests that consume the repository
/// transitively (e.g. <c>DefaultAgentCloningPolicyEnforcerTests</c>).
/// </summary>
/// <remarks>
/// Keys mirror the EF entity discriminator: <c>(scope, normalised id)</c>.
/// Tenant scope uses a sentinel string so a Guid target id and a non-Guid
/// target id collapse into the same slot — matching the EF impl which
/// ignores the target id for tenant scope and pins on the ambient tenant.
/// An all-null policy (<see cref="AgentCloningPolicy.IsEmpty"/>) is
/// removed rather than stored to mirror the production "delete on Empty"
/// contract.
/// </remarks>
public class InMemoryAgentCloningPolicyRepository : IAgentCloningPolicyRepository
{
    private const string TenantSentinel = "__tenant__";

    private readonly ConcurrentDictionary<(CloningPolicyScope Scope, string Key), AgentCloningPolicy> _slots = new();

    /// <inheritdoc />
    public Task<AgentCloningPolicy> GetAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var key = NormaliseKey(scope, targetId);
        return Task.FromResult(_slots.TryGetValue((scope, key), out var policy) ? policy : AgentCloningPolicy.Empty);
    }

    /// <inheritdoc />
    public Task SetAsync(
        CloningPolicyScope scope,
        string targetId,
        AgentCloningPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(policy);

        var key = NormaliseKey(scope, targetId);
        if (policy.IsEmpty)
        {
            _slots.TryRemove((scope, key), out _);
            return Task.CompletedTask;
        }

        _slots[(scope, key)] = policy;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(
        CloningPolicyScope scope,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var key = NormaliseKey(scope, targetId);
        _slots.TryRemove((scope, key), out _);
        return Task.CompletedTask;
    }

    private static string NormaliseKey(CloningPolicyScope scope, string targetId) => scope switch
    {
        // Agent scope: the EF impl parses the id to a Guid. Normalise the
        // string form so callers can pass either "D" or "N" Guid formats.
        CloningPolicyScope.Agent => Guid.TryParse(targetId, out var guid)
            ? guid.ToString("D")
            : targetId,
        // Tenant scope: the EF impl ignores the target id (it pins on the
        // ambient tenant). Collapse all writes to a single slot.
        CloningPolicyScope.Tenant => TenantSentinel,
        _ => targetId,
    };
}
