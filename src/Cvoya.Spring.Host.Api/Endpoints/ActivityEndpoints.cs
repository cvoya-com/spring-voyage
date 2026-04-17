// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Reactive.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps activity-related API endpoints for querying and streaming activity events.
/// </summary>
/// <remarks>
/// <para>
/// The SSE endpoint subscribes to the reactive observable graph — either the
/// platform-wide <see cref="IActivityEventBus.ActivityStream"/> (when no unit
/// is specified) or the per-unit projection from
/// <see cref="IUnitActivityObservable"/>. Permission checks run <strong>once
/// at subscribe time</strong> for unit-scoped streams (issue #391): the
/// caller's effective permission is resolved against the target unit before
/// events start flowing, and unauthorized callers are rejected with 403 — no
/// events reach the wire. For the unscoped platform stream, a per-source
/// permission cache avoids recomputing authorisation per event without
/// falling back to synchronous actor calls on the hot path.
/// </para>
/// </remarks>
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
            .WithSummary("Query activity events with filters and pagination")
            .Produces<ActivityQueryResult>(StatusCodes.Status200OK);

        // SSE stream — no body schema; the wire format is event-stream.
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
        IUnitActivityObservable unitActivityObservable,
        IPermissionService permissionService,
        ILoggerFactory loggerFactory,
        string? source,
        string? severity,
        string? unitId,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.ActivityEndpoints");

        var humanId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(humanId))
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Build the source observable once — unit-scoped when the caller
        // passes ?unitId=..., platform-wide otherwise. Permission checks
        // run before the SSE stream begins, so an unauthorized caller
        // gets 403 instead of an empty stream that silently filters
        // every event.
        IObservable<ActivityEvent> stream;
        if (!string.IsNullOrEmpty(unitId))
        {
            var permission = await permissionService
                .ResolvePermissionAsync(humanId, unitId, cancellationToken);

            if (permission is null || permission.Value < PermissionLevel.Viewer)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            stream = await unitActivityObservable.GetStreamAsync(unitId, cancellationToken);
        }
        else
        {
            stream = activityEventBus.ActivityStream;
            // Per-event permission enforcement for the platform-wide stream.
            // Resolution is cached per-(source-scheme,source-path) for the
            // lifetime of the subscription so a chatty agent inside an
            // authorised unit doesn't cause a storm of actor proxy calls.
            stream = ApplyPlatformPermissionFilter(stream, humanId, permissionService, logger);
        }

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

        // Bounded channel decouples the Rx producer from the HTTP writer:
        // the subscription drops into a fixed-size queue, and a single
        // writer loop drains it in FIFO order. DropOldest handles the
        // worst-case burst without blocking the producer thread that Rx.NET
        // uses for OnNext.
        var channel = Channel.CreateBounded<ActivityEvent>(new BoundedChannelOptions(capacity: 256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        using var subscription = stream.Subscribe(
            evt =>
            {
                if (!channel.Writer.TryWrite(evt))
                {
                    // The channel is already completed — subscriber will dispose shortly.
                }
            },
            ex =>
            {
                logger.LogWarning(ex, "Activity SSE stream faulted for human {HumanId}.", humanId);
                channel.Writer.TryComplete(ex);
            },
            () => channel.Writer.TryComplete());

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        // Flush headers up front so clients (dashboards, CLI, test harnesses)
        // can treat ResponseHeadersRead completion as the "subscription is
        // live" signal. The Rx subscription above is already receiving events
        // into the channel by the time the flush returns.
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(evt);
                await httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected.
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Applies a per-source permission filter to the platform-wide stream,
    /// resolving each source's authorisation at most once per subscription.
    /// Unit sources that aren't authorised are dropped; agent, human, and
    /// tenant sources pass through — permission is enforced at the unit the
    /// caller is trying to observe, not at every descendant event.
    /// </summary>
    private static IObservable<ActivityEvent> ApplyPlatformPermissionFilter(
        IObservable<ActivityEvent> source,
        string humanId,
        IPermissionService permissionService,
        ILogger logger)
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        return source.Where(evt =>
        {
            if (!evt.Source.Scheme.Equals("unit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return cache.GetOrAdd(evt.Source.Path, unitPath =>
            {
                try
                {
                    var permission = permissionService
                        .ResolvePermissionAsync(humanId, unitPath, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    return permission.HasValue && permission.Value >= PermissionLevel.Viewer;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Permission lookup failed for human {HumanId} on unit {UnitId}; denying.",
                        humanId, unitPath);
                    return false;
                }
            });
        });
    }
}