// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Implements clone cost attribution by persisting cost records as <see cref="ActivityEventRecord"/> entities
/// with a source format of "agent:{parentAgentId}/clone:{cloneId}".
/// Aggregation sums all costs where the source starts with "agent:{agentId}".
/// </summary>
public class CloneCostTracker(SpringDbContext dbContext) : ICostTracker
{
    /// <inheritdoc />
    public async Task AttributeCostAsync(string parentAgentId, string cloneId, decimal cost, CancellationToken cancellationToken)
    {
        var record = new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            Source = $"agent:{parentAgentId}/clone:{cloneId}",
            EventType = "CostIncurred",
            Severity = "Information",
            Summary = $"Clone {cloneId} cost attributed to agent {parentAgentId}",
            Cost = cost,
            Timestamp = DateTimeOffset.UtcNow
        };

        dbContext.ActivityEvents.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<decimal> GetAggregatedCostAsync(string agentId, CancellationToken cancellationToken)
    {
        var prefix = $"agent:{agentId}";

        return await dbContext.ActivityEvents
            .Where(e => e.Source.StartsWith(prefix) && e.Cost.HasValue)
            .SumAsync(e => e.Cost!.Value, cancellationToken);
    }
}