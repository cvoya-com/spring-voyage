// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Implements clone cost attribution by persisting cost records as
/// <see cref="ActivityEventRecord"/> entities. The parent agent's Guid id
/// is stored in <see cref="ActivityEventRecord.SourceId"/>; aggregation
/// sums all costs whose source matches the parent agent's id. The clone
/// identity is recorded in the event summary for audit; per-clone roll-up
/// is the rendering layer's concern (#1635-class follow-ups).
/// </summary>
public class CloneCostTracker(SpringDbContext dbContext) : ICostTracker
{
    /// <inheritdoc />
    public async Task AttributeCostAsync(string parentAgentId, string cloneId, decimal cost, CancellationToken cancellationToken)
    {
        if (!GuidFormatter.TryParse(parentAgentId, out var parentAgentUuid))
        {
            throw new ArgumentException(
                $"parentAgentId '{parentAgentId}' is not a valid Guid.",
                nameof(parentAgentId));
        }

        var record = new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            SourceId = parentAgentUuid,
            EventType = "CostIncurred",
            Severity = "Information",
            Summary = $"Clone {cloneId} cost attributed to agent {parentAgentId}",
            Cost = cost,
            Timestamp = DateTimeOffset.UtcNow,
        };

        dbContext.ActivityEvents.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<decimal> GetAggregatedCostAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!GuidFormatter.TryParse(agentId, out var agentUuid))
        {
            return 0m;
        }

        return await dbContext.ActivityEvents
            .Where(e => e.SourceId == agentUuid && e.Cost.HasValue)
            .SumAsync(e => e.Cost!.Value, cancellationToken);
    }
}
