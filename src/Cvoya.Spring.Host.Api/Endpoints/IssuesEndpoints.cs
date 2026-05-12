// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Issues;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// #2160: read endpoints for the operational-issues surface. One pair
/// per subject kind (unit / agent), each returning the subject's own
/// open issues plus the transitively-aggregated descendant rollup.
/// Producers write through <see cref="IIssueWriter"/>; this surface is
/// read-only — manual ack / dismiss is tracked under #2174 (v0.2).
/// </summary>
public static class IssuesEndpoints
{
    /// <summary>
    /// Registers the issues read endpoints. Call from <c>Program.cs</c>
    /// alongside the other tenant-scoped endpoint groups.
    /// </summary>
    public static RouteGroupBuilder MapIssuesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty);

        group.MapGet("/api/v1/tenant/units/{id}/issues", GetUnitIssuesAsync)
            .WithTags("Units")
            .WithName("GetUnitIssues")
            .WithSummary("Open operational issues against a unit, plus the transitively-aggregated descendant rollup.")
            .Produces<IssuesViewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/agents/{id}/issues", GetAgentIssuesAsync)
            .WithTags("Agents")
            .WithName("GetAgentIssues")
            .WithSummary("Open operational issues against an agent. Agents have no descendants — the rollup is always empty.")
            .Produces<IssuesViewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetUnitIssuesAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IIssueAggregator aggregator,
        [FromQuery] bool? includeDescendants,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("unit", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Unit '{id}' not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var view = await aggregator.AggregateForUnitAsync(entry.ActorId, cancellationToken);
        var includeRollup = includeDescendants ?? true;
        return Results.Ok(ToResponse(view, includeRollup));
    }

    private static async Task<IResult> GetAgentIssuesAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IIssueAggregator aggregator,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(
                detail: $"Agent '{id}' not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var view = await aggregator.AggregateForAgentAsync(entry.ActorId, cancellationToken);
        // Agents have no descendants; preserve the response shape so
        // the portal can render a uniform envelope.
        return Results.Ok(ToResponse(view, includeDescendants: true));
    }

    private static readonly IssueDescendantRollupResponse EmptyDescendantRollup =
        new(0, 0, Array.Empty<IssueChildSummaryResponse>());

    private static IssuesViewResponse ToResponse(IssuesView view, bool includeDescendants) =>
        new(
            Own: view.Own.Select(ToResponse).ToList(),
            Descendants: includeDescendants
                ? ToResponse(view.Descendants)
                : EmptyDescendantRollup);

    private static IssueResponse ToResponse(Issue issue) =>
        new(
            Id: issue.Id,
            SubjectKind: FormatKind(issue.Subject.Kind),
            SubjectId: issue.Subject.Id,
            Severity: FormatSeverity(issue.Severity),
            Source: issue.Source,
            Code: issue.Code,
            Title: issue.Title,
            Detail: issue.Detail,
            TraceId: issue.TraceId,
            CreatedAt: issue.CreatedAt,
            UpdatedAt: issue.UpdatedAt);

    private static IssueDescendantRollupResponse ToResponse(IssueDescendantRollup rollup) =>
        new(
            ErrorCount: rollup.ErrorCount,
            WarningCount: rollup.WarningCount,
            ByChild: rollup.ByChild
                .Select(c => new IssueChildSummaryResponse(
                    SubjectKind: FormatKind(c.Subject.Kind),
                    SubjectId: c.Subject.Id,
                    Name: c.Name,
                    ErrorCount: c.ErrorCount,
                    WarningCount: c.WarningCount))
                .ToList());

    private static string FormatKind(IssueSubjectKind kind) => kind switch
    {
        IssueSubjectKind.Unit => "unit",
        IssueSubjectKind.Agent => "agent",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string FormatSeverity(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "error",
        IssueSeverity.Warning => "warning",
        _ => severity.ToString().ToLowerInvariant(),
    };
}
