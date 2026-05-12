// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System;

using Cvoya.Spring.Core.Issues;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Row in <c>issues</c> — one operationally-significant condition
/// observed about a unit or an agent (#2160). Producers
/// (<c>IIssueWriter</c>) upsert keyed on
/// <c>(TenantId, SubjectKind, SubjectId, Source, Code)</c>; the same
/// producer marks <see cref="ClearedAt"/> when the condition is fixed.
/// Consumers (<c>IIssueReader</c>, the API endpoints, the aggregator)
/// filter on <c>ClearedAt is null</c> for the open set. Cleared rows
/// are kept short-term for audit; periodic GC will drop them once a
/// retention story lands (tracked under #2174).
/// </summary>
public class IssueEntity : ITenantScopedEntity
{
    /// <summary>Server-issued identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns this row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Whether the subject is a unit or an agent.</summary>
    public IssueSubjectKind SubjectKind { get; set; }

    /// <summary>Unit / agent definition id the issue is reported against.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Severity bucket — Error blocks, Warning advises.</summary>
    public IssueSeverity Severity { get; set; }

    /// <summary>
    /// Producer label — <c>"validation"</c>, <c>"runtime"</c>,
    /// <c>"credential"</c>, <c>"configuration"</c>, … Used to scope
    /// <c>IIssueWriter.ClearAsync</c> calls.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Stable code (matches the translator's lookup key).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Operator-readable summary; never raw JSON.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional second sentence with what to do next.</summary>
    public string? Detail { get; set; }

    /// <summary>Optional W3C trace id of the operation that produced the issue.</summary>
    public string? TraceId { get; set; }

    /// <summary>UTC instant the producer first observed the condition.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// UTC instant the producer last re-observed the condition. Equal
    /// to <see cref="CreatedAt"/> until the producer re-fires.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// UTC instant the producer marked the condition fixed. Null while
    /// the issue is open. Open-set queries filter on this column.
    /// </summary>
    public DateTimeOffset? ClearedAt { get; set; }
}
