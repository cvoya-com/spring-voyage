// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Issues;

using System;

using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// One operationally-significant condition observed about a unit or an
/// agent — surfaced on the Overview tab and through
/// <c>spring (unit|agent) issues</c>. Producers (validation workflow,
/// dispatch path, runtime supervisor, …) open issues as Errors or
/// Warnings; the same producer clears the entry when the underlying
/// condition is fixed (#2160). The transitive aggregator rolls a unit's
/// own issues together with descendants' so the Overview tab shows the
/// full operational picture for a hierarchy at a glance.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the existing
/// <see cref="Cvoya.Spring.Core.Lifecycle.ArtefactValidationError"/>: that
/// type is the *transient* outcome of the most recent validation
/// probe, replaced on every Validating transition. <see cref="Issue"/>
/// is the *durable* set of currently-active concerns from any source —
/// validation is one such source today, with runtime / dispatch /
/// configuration sources to follow in PR B.
/// </para>
/// <para>
/// Manual ack / dismiss is deliberately not modelled in v0.1 — every
/// open issue is producer-cleared. See #2174 for the v0.2 follow-up
/// that adds operator dismissal.
/// </para>
/// </remarks>
/// <param name="Id">Server-issued identity for the issue row. Stable across reads.</param>
/// <param name="Subject">The unit or agent the issue is reported against.</param>
/// <param name="Severity">Error (blocking) or Warning (non-blocking advisory).</param>
/// <param name="Source">
/// Producer label — <c>"validation"</c>, <c>"runtime"</c>,
/// <c>"credential"</c>, <c>"configuration"</c>, … The translator uses
/// <see cref="Code"/> (not source) as its lookup key; source is for
/// grouping in the Overview UI and for debugging.
/// </param>
/// <param name="Code">
/// Stable code consumers branch on — same shape as the existing
/// <see cref="Cvoya.Spring.Core.Lifecycle.ArtefactValidationCodes"/> values,
/// extended with codes from non-validation producers (e.g. <c>image-pull-failed</c>,
/// <c>credential-rejected</c>). The translator (CLI + portal) maps codes
/// to friendly title + advice.
/// </param>
/// <param name="Title">Operator-readable summary; never raw JSON.</param>
/// <param name="Detail">Optional second sentence with what to do next.</param>
/// <param name="TraceId">
/// Optional W3C trace id of the operation that produced the issue.
/// Carried so the Overview disclosure can offer a "support correlation"
/// surface without coupling the rendering layer to OpenTelemetry.
/// </param>
/// <param name="CreatedAt">When the producer first observed the condition.</param>
/// <param name="UpdatedAt">When the producer last re-observed the condition (e.g. re-fired the same code without clearing).</param>
public sealed record Issue(
    Guid Id,
    IssueSubject Subject,
    IssueSeverity Severity,
    string Source,
    string Code,
    string Title,
    string? Detail,
    string? TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Severity bucket the Overview UI uses to colour and group issues.
/// Closed enum — adding a third bucket (Info) is a v0.2 design call.
/// </summary>
public enum IssueSeverity
{
    /// <summary>Blocking condition — the unit/agent cannot do its job until cleared.</summary>
    Error = 0,

    /// <summary>Non-blocking advisory — the operator should be aware but operation continues.</summary>
    Warning = 1,
}

/// <summary>
/// Identity of the unit or agent an <see cref="Issue"/> is reported
/// against. Polymorphic so producers and consumers can share one API
/// shape across both kinds without duplicating endpoints.
/// </summary>
/// <param name="Kind">Whether the subject is a unit or an agent.</param>
/// <param name="Id">Definition id — <c>UnitDefinitionEntity.Id</c> or <c>AgentDefinitionEntity.Id</c>.</param>
public sealed record IssueSubject(IssueSubjectKind Kind, Guid Id);

/// <summary>
/// Discriminator for <see cref="IssueSubject"/>. Closed enum.
/// </summary>
public enum IssueSubjectKind
{
    /// <summary>Subject is a unit definition.</summary>
    Unit = 0,

    /// <summary>Subject is an agent definition.</summary>
    Agent = 1,
}

/// <summary>
/// One issue together with the rolled-up counts of issues observed on
/// the subject's descendants (a unit's child units + member agents).
/// Returned by <c>GET /api/v1/tenant/(units|agents)/{id}/issues</c> when
/// <c>includeDescendants=true</c> (the default for units; always false
/// for agents — agents have no descendants).
/// </summary>
/// <param name="Own">Issues reported directly against the subject.</param>
/// <param name="Descendants">Aggregated counts + per-child summaries from descendants.</param>
public sealed record IssuesView(
    IReadOnlyList<Issue> Own,
    IssueDescendantRollup Descendants);

/// <summary>
/// Transitive issue counts for a unit's descendants, plus per-child
/// summaries so the Overview UI can offer drill-down navigation
/// without a second round-trip.
/// </summary>
/// <param name="ErrorCount">Total Error-severity issues across all descendants.</param>
/// <param name="WarningCount">Total Warning-severity issues across all descendants.</param>
/// <param name="ByChild">One entry per immediate child that has any issues; recursion happens server-side.</param>
public sealed record IssueDescendantRollup(
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<IssueChildSummary> ByChild);

/// <summary>
/// Summary of issues observed against one immediate child of the
/// subject — used by the parent's Overview UI to render a list of
/// "child has problems" links without duplicating the full issue
/// payload server-side.
/// </summary>
/// <param name="Subject">The child unit or agent.</param>
/// <param name="Name">Display name for the child, so the UI doesn't need a second fetch.</param>
/// <param name="ErrorCount">Errors against this child plus its own descendants.</param>
/// <param name="WarningCount">Warnings against this child plus its own descendants.</param>
public sealed record IssueChildSummary(
    IssueSubject Subject,
    string Name,
    int ErrorCount,
    int WarningCount);
