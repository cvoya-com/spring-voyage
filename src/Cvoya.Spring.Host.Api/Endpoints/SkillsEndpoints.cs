// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Maps the platform-wide skill catalog endpoint. Per-agent skill
/// configuration (read / replace) lives on the agent's own routes
/// (<c>GET/PUT /api/v1/agents/{id}/skills</c>) because skills are
/// agent-owned; this endpoint exposes the discovery side so the UI can
/// present available skills as a picker.
/// </summary>
public static class SkillsEndpoints
{
    /// <summary>
    /// Registers skill-catalog endpoints on the specified endpoint route
    /// builder.
    /// </summary>
    public static RouteGroupBuilder MapSkillsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/skills")
            .WithTags("Skills");

        group.MapGet("/", ListSkillsAsync)
            .WithName("ListSkills")
            .WithSummary("List every tool exposed by every registered skill registry")
            .Produces<SkillCatalogEntry[]>(StatusCodes.Status200OK);

        return group;
    }

    private static Task<IResult> ListSkillsAsync(
        IEnumerable<ISkillRegistry> registries,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Flatten (registry × tool) pairs into a single list. Order is
        // registry name, then tool name — stable so operator UIs don't
        // reorder on refresh.
        var entries = registries
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .SelectMany(r => r.GetToolDefinitions()
                .Select(t => new SkillCatalogEntry(t.Name, t.Description, r.Name))
                .OrderBy(e => e.Name, StringComparer.Ordinal))
            .ToList();

        return Task.FromResult<IResult>(Results.Ok(entries));
    }
}