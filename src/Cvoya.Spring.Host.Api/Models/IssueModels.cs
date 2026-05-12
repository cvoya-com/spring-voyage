// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Wire shape for <c>GET /api/v1/tenant/(units|agents)/{id}/issues</c>
/// (#2160). Carries the subject's own open issues plus the
/// transitively-aggregated descendant rollup. The rollup block is
/// omitted (null on the wire) when the caller passed
/// <c>?includeDescendants=false</c> so single-row consumers can opt out
/// of the membership walk.
/// </summary>
/// <param name="Own">Currently-open issues against the requested subject.</param>
/// <param name="Descendants">
/// Roll-up across descendants. When <c>?includeDescendants=false</c> is
/// supplied, the rollup is returned with zero counts and an empty
/// per-child list so the response shape stays uniform across callers
/// (avoids generating a sum-type wrapper in strongly-typed Kiota
/// clients).
/// </param>
public sealed record IssuesViewResponse(
    [property: JsonPropertyName("own")] IReadOnlyList<IssueResponse> Own,
    [property: JsonPropertyName("descendants")] IssueDescendantRollupResponse Descendants);

/// <summary>
/// One open issue against a unit or agent (#2160).
/// </summary>
/// <param name="Id">Server-issued issue identity.</param>
/// <param name="SubjectKind">"unit" or "agent".</param>
/// <param name="SubjectId">Definition id of the unit or agent.</param>
/// <param name="Severity">"error" (blocking) or "warning" (advisory).</param>
/// <param name="Source">Producer label — "validation", "runtime", "credential", "configuration", …</param>
/// <param name="Code">Stable code consumers branch on; matches the translator's lookup key.</param>
/// <param name="Title">Operator-readable summary.</param>
/// <param name="Detail">Optional second sentence with what to do next.</param>
/// <param name="TraceId">Optional W3C trace id of the operation that produced the issue.</param>
/// <param name="CreatedAt">When the producer first observed the condition.</param>
/// <param name="UpdatedAt">When the producer last re-observed the condition.</param>
public sealed record IssueResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("subjectKind")] string SubjectKind,
    [property: JsonPropertyName("subjectId")] Guid SubjectId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("traceId")] string? TraceId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>
/// Aggregated open-issue counts for the subject's descendants (#2160),
/// plus per-immediate-child summaries the Overview UI uses to drill
/// into the source of the rollup. Always empty for agents.
/// </summary>
/// <param name="ErrorCount">Total Error-severity issues across all descendants.</param>
/// <param name="WarningCount">Total Warning-severity issues across all descendants.</param>
/// <param name="ByChild">One entry per immediate child that has issues; clean children are omitted.</param>
public sealed record IssueDescendantRollupResponse(
    [property: JsonPropertyName("errorCount")] int ErrorCount,
    [property: JsonPropertyName("warningCount")] int WarningCount,
    [property: JsonPropertyName("byChild")] IReadOnlyList<IssueChildSummaryResponse> ByChild);

/// <summary>
/// Per-immediate-child issue summary used by the Overview UI's
/// "drill-down" affordance. Counts include the child's own issues plus
/// every descendant of that child.
/// </summary>
/// <param name="SubjectKind">"unit" or "agent".</param>
/// <param name="SubjectId">Definition id of the immediate child.</param>
/// <param name="Name">Child's display name.</param>
/// <param name="ErrorCount">Errors against this child plus its own descendants.</param>
/// <param name="WarningCount">Warnings against this child plus its own descendants.</param>
public sealed record IssueChildSummaryResponse(
    [property: JsonPropertyName("subjectKind")] string SubjectKind,
    [property: JsonPropertyName("subjectId")] Guid SubjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("errorCount")] int ErrorCount,
    [property: JsonPropertyName("warningCount")] int WarningCount);
