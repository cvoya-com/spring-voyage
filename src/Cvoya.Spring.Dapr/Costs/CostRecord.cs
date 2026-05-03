// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Represents a persisted cost record for a single AI provider interaction.
/// Tracks token usage, cost, and duration per agent, unit, and tenant.
/// </summary>
public class CostRecord : ITenantScopedEntity
{
    /// <summary>Gets or sets the unique identifier for the cost record.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this cost record.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Gets or sets the agent (Guid) that incurred this cost.</summary>
    public Guid AgentId { get; set; }

    /// <summary>Gets or sets the unit Guid the agent belongs to, if any.</summary>
    public Guid? UnitId { get; set; }

    /// <summary>Gets or sets the AI model used for the completion.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of input tokens consumed.</summary>
    public int InputTokens { get; set; }

    /// <summary>Gets or sets the number of output tokens generated.</summary>
    public int OutputTokens { get; set; }

    /// <summary>Gets or sets the estimated cost in USD.</summary>
    public decimal Cost { get; set; }

    /// <summary>Gets or sets the wall-clock duration of the completion, if available.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Gets or sets the timestamp when the cost was incurred.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Gets or sets the correlation identifier for tracing related events.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the origin of this cost (normal work vs. initiative loop).
    /// Tagged at emission time by <c>AgentActor</c>; defaults to
    /// <see cref="CostSource.Work"/> for records written before the split was tracked.
    /// </summary>
    public CostSource Source { get; set; } = CostSource.Work;
}