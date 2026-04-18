// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

/// <summary>
/// No-op <see cref="IOrchestrationStrategyCacheInvalidator"/>. Registered as
/// the default so write-path callers can always resolve the interface and
/// invoke it unconditionally — even in hosts that have not registered a
/// caching <see cref="IOrchestrationStrategyProvider"/> decorator.
/// </summary>
public sealed class NullOrchestrationStrategyCacheInvalidator : IOrchestrationStrategyCacheInvalidator
{
    /// <summary>
    /// Shared singleton — there is no state to hold.
    /// </summary>
    public static readonly NullOrchestrationStrategyCacheInvalidator Instance = new();

    /// <inheritdoc />
    public void Invalidate(string unitId)
    {
        // Intentionally empty — no cache is present.
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        // Intentionally empty — no cache is present.
    }
}