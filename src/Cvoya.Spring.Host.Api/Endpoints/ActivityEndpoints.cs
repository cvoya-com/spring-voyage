// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Reactive.Linq;
using System.Security.Claims;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps activity-related API endpoints for querying and streaming activity events.
/// </summary>
public static class ActivityEndpoints
{
    /// <summary>
    /// Registers activity endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/activity")
            .WithTags("Activity");

        group.MapGet("/", QueryActivityAsync)
            .WithName("QueryActivity")
            .WithSummary("Query activity events with filters and pagination");

        group.MapGet("/stream", StreamActivityAsync)
            .WithName("StreamActivity")
            .WithSummary("Stream activity events via SSE");

        return group;
    }

    private static async Task<IResult> QueryActivityAsync(
        [AsParameters] ActivityQueryParametersDto query,
        IActivityQueryService queryService,
        CancellationToken cancellationToken)
    {
        var parameters = new ActivityQueryParameters(
            query.Source, query.EventType, query.Severity,
            query.From, query.To, query.Page ?? 1, query.PageSize ?? 50);
        var result = await queryService.QueryAsync(parameters, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task StreamActivityAsync(
        HttpContext httpContext,
        IActivityEventBus activityEventBus,
        IPermissionService permissionService,
        string? source,
        string? severity,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        var humanId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(humanId))
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var stream = activityEventBus.ActivityStream;

        // Apply permission-based filtering: only show events from sources the user can observe.
        stream = stream.Where(evt => IsAuthorizedToObserve(evt, humanId, permissionService));

        // Apply query parameter filters.
        if (!string.IsNullOrEmpty(source))
        {
            stream = stream.Where(evt =>
                $"{evt.Source.Scheme}://{evt.Source.Path}".Equals(source, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(severity) &&
            Enum.TryParse<ActivitySeverity>(severity, ignoreCase: true, out var severityFilter))
        {
            stream = stream.Where(evt => evt.Severity >= severityFilter);
        }

        var tcs = new TaskCompletionSource();
        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());

        using var subscription = stream
            .Subscribe(
                evt =>
                {
                    var json = JsonSerializer.Serialize(evt);
                    // Fire-and-forget write within the subscription callback.
                    // HttpContext response writing is inherently sequential per request.
                    httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken)
                        .ContinueWith(_ => httpContext.Response.Body.FlushAsync(cancellationToken), cancellationToken);
                },
                ex => tcs.TrySetException(ex),
                () => tcs.TrySetResult());

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected.
        }
    }

    /// <summary>
    /// Checks whether the requesting user is authorized to observe events from the given source.
    /// Unit-sourced events require at least Viewer permission; agent and other events are allowed by default.
    /// </summary>
    private static bool IsAuthorizedToObserve(ActivityEvent evt, string humanId, IPermissionService permissionService)
    {
        // Only unit-sourced events require permission checks.
        if (!evt.Source.Scheme.Equals("unit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Synchronous permission resolution for the Rx pipeline.
        // PermissionService is designed for fast lookups (actor proxy call).
        var permission = permissionService
            .ResolvePermissionAsync(humanId, evt.Source.Path, CancellationToken.None)
            .GetAwaiter().GetResult();

        return permission.HasValue && permission.Value >= PermissionLevel.Viewer;
    }
}