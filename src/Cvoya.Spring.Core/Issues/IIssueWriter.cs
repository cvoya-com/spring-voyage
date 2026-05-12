// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Issues;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Producer surface (#2160). Producers — the validation workflow,
/// dispatch path, runtime supervisor, configuration drift detector —
/// call <see cref="UpsertAsync"/> when they observe an
/// operationally-significant condition, and <see cref="ClearAsync"/>
/// when the condition is fixed. Issues are keyed on
/// <c>(Subject, Source, Code)</c> so a re-firing producer doesn't
/// create duplicate rows; instead the existing row's
/// <see cref="Issue.UpdatedAt"/> is bumped and the title/detail can be
/// refreshed.
/// </summary>
/// <remarks>
/// <para>
/// Producer-driven clearing is the v0.1 model — the same producer that
/// opened the issue is responsible for clearing it. Manual operator
/// dismiss is tracked separately (#2174 / v0.2).
/// </para>
/// <para>
/// The interface is a DI seam. The OSS default is the EF-backed
/// <c>IssueRepository</c>; cloud hosts may replace it with a
/// tenant-scoped variant.
/// </para>
/// </remarks>
public interface IIssueWriter
{
    /// <summary>
    /// Open a new issue or refresh an existing one keyed on
    /// <c>(Subject, Source, Code)</c>. When an open row already
    /// matches, this is a no-op on identity but updates
    /// <see cref="Issue.UpdatedAt"/>, <see cref="Issue.Title"/>,
    /// <see cref="Issue.Detail"/>, <see cref="Issue.TraceId"/>, and
    /// <see cref="Issue.Severity"/> in place. Returns the persisted
    /// row.
    /// </summary>
    Task<Issue> UpsertAsync(
        IssueSubject subject,
        IssueSeverity severity,
        string source,
        string code,
        string title,
        string? detail,
        string? traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark every open issue against <paramref name="subject"/> with
    /// the matching <paramref name="source"/> as cleared. Optional
    /// <paramref name="code"/> narrows the clear to one specific code
    /// (e.g. clear only the <c>image-pull-failed</c> row, leave other
    /// runtime-source rows alone). Idempotent — clearing already-
    /// cleared rows is a no-op.
    /// </summary>
    Task ClearAsync(
        IssueSubject subject,
        string source,
        string? code = null,
        CancellationToken cancellationToken = default);
}
