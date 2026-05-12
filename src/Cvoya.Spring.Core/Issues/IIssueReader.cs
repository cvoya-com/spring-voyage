// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Issues;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Consumer surface (#2160). Read-only counterpart of
/// <see cref="IIssueWriter"/>. The API endpoints, the transitive
/// aggregator, and the tree-explorer badge query all read through this
/// interface so the storage layer stays a single DI seam.
/// </summary>
public interface IIssueReader
{
    /// <summary>
    /// All currently-open issues against the given subject. The list
    /// is ordered by <see cref="Issue.Severity"/> then
    /// <see cref="Issue.UpdatedAt"/> descending so the most recent
    /// blocking concerns surface first.
    /// </summary>
    Task<IReadOnlyList<Issue>> ListOpenAsync(
        IssueSubject subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Currently-open issue counts for many subjects in one round-trip.
    /// Used by the tree-explorer to render badges across an entire
    /// hierarchy without a per-row fetch. Subjects with no open issues
    /// are omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<IssueSubject, IssueCounts>> CountOpenAsync(
        IReadOnlyCollection<IssueSubject> subjects,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Open-issue tally for one subject, broken down by severity.
/// </summary>
public sealed record IssueCounts(int ErrorCount, int WarningCount)
{
    /// <summary>True when both buckets are zero.</summary>
    public bool IsEmpty => ErrorCount == 0 && WarningCount == 0;
}
