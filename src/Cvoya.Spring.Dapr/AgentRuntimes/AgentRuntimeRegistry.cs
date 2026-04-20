// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.AgentRuntimes;

using Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Default <see cref="IAgentRuntimeRegistry"/> implementation. Enumerates
/// every <see cref="IAgentRuntime"/> instance registered in DI and serves
/// them through a stable snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The registry takes its runtime list from the DI container at
/// construction time, which matches the singleton lifetime of the service:
/// runtimes are expected to be registered once at host startup and never
/// added or removed at runtime. For tests, inject a custom
/// <see cref="IEnumerable{IAgentRuntime}"/> directly.
/// </para>
/// <para>
/// <see cref="Get(string)"/> matches case-insensitively on
/// <see cref="IAgentRuntime.Id"/>. Duplicate ids are resolved by first
/// match; duplicates indicate a DI registration bug and callers should
/// surface them as errors upstream.
/// </para>
/// </remarks>
public class AgentRuntimeRegistry : IAgentRuntimeRegistry
{
    private readonly IReadOnlyList<IAgentRuntime> _runtimes;

    /// <summary>
    /// Creates a new registry over the supplied runtimes. Typically
    /// constructed by DI with every registered <see cref="IAgentRuntime"/>.
    /// </summary>
    /// <param name="runtimes">The runtimes to expose through the registry.</param>
    public AgentRuntimeRegistry(IEnumerable<IAgentRuntime> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        _runtimes = runtimes.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentRuntime> All => _runtimes;

    /// <inheritdoc />
    public IAgentRuntime? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        for (var i = 0; i < _runtimes.Count; i++)
        {
            var runtime = _runtimes[i];
            if (string.Equals(runtime.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return runtime;
            }
        }

        return null;
    }
}