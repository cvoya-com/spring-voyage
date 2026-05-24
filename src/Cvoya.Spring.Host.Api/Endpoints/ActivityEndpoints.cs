// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Reactive.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
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
        var group = app.MapGroup("/api/v1/tenant/activity")
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
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        // #987: the portal tenant tree surfaces units/agents by slug, so the
        // Activity tab queries with `source=unit:<slug>` / `agent:<slug>`.
        // Events are persisted with `source={scheme}:{actorId}`, so without
        // normalization every slug-based query returned an empty page.
        var normalizedSource = await ActivitySourceNormalizer
            .NormalizeQuerySourceAsync(query.Source, directoryService, cancellationToken);

        var parameters = new ActivityQueryParameters(
            normalizedSource, query.EventType, query.Severity,
            query.From, query.To, query.Page ?? 1, query.PageSize ?? 50);
        var result = await queryService.QueryAsync(parameters, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task StreamActivityAsync(
        HttpContext httpContext,
        IActivityEventBus activityEventBus,
        IUnitActivityObservable unitActivityObservable,
        IPermissionService permissionService,
        IDirectoryService directoryService,
        ILoggerFactory loggerFactory,
        string? source,
        string? severity,
        string? unitId,
        string? thread,
        string? message,
        string[]? kind,
        DateTimeOffset? from,
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
            // Hierarchy-aware (#414): a Viewer on an ancestor can observe
            // the activity of any descendant unit unless the descendant is
            // marked UnitPermissionInheritance.Isolated.
            var permission = await permissionService
                .ResolveEffectivePermissionAsync(humanId, unitId, cancellationToken);

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
            // #987: accept `source=unit:<slug-or-uuid>` / `agent:<slug-or-uuid>`
            // and rewrite to the `{scheme}://{actorId}` form the stream
            // filter compares against — the stream emits the actor's
            // Dapr id as `Source.Path`, not the slug.
            var normalizedSource = await ActivitySourceNormalizer
                .NormalizeStreamSourceAsync(source, directoryService, cancellationToken);
            stream = stream.Where(evt =>
                $"{evt.Source.Scheme}://{evt.Source.Path}".Equals(normalizedSource, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(severity) &&
            Enum.TryParse<ActivitySeverity>(severity, ignoreCase: true, out var severityFilter))
        {
            stream = stream.Where(evt => evt.Severity >= severityFilter);
        }

        // Thread-scoped filter: when ?thread=<id> is supplied, only events
        // whose CorrelationId matches are forwarded. This is the foundation
        // for engagement-level observability (#1421) — the CorrelationId on
        // ActivityEvent carries the thread id that the messaging layer stamps
        // on every event for a given thread.
        if (!string.IsNullOrEmpty(thread))
        {
            stream = stream.Where(evt =>
                string.Equals(evt.CorrelationId, thread, StringComparison.Ordinal));
        }

        // Message-scoped filter (#2492): forward only events whose Details
        // payload carries a matching `sv.message.id` resource attribute or
        // top-level `messageId` key. Matched case-insensitively so the
        // OTLP runtime path (which stamps the canonical no-dash hex form)
        // and the in-process path (which sometimes stamps dashed) both
        // line up.
        if (!string.IsNullOrEmpty(message))
        {
            stream = stream.Where(evt => MatchesMessage(evt, message));
        }

        // Event-kind multiselect (#2492). Accepts repeated `?kind=` query
        // params; only events whose typed EventType matches one of the
        // supplied names (case-insensitive) are forwarded.
        if (kind is { Length: > 0 })
        {
            var allowed = new HashSet<string>(kind, StringComparer.OrdinalIgnoreCase);
            stream = stream.Where(evt => allowed.Contains(evt.EventType.ToString()));
        }

        // Time-window lower bound (#2492). The SSE channel is "now-forward"
        // by nature, but this filter is useful when a CLI reconnects after a
        // network blip and wants to skip events older than its last seen.
        if (from is { } fromTs)
        {
            stream = stream.Where(evt => evt.Timestamp >= fromTs);
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

    private static bool MatchesMessage(ActivityEvent evt, string messageId)
    {
        if (evt.Details is null) return false;
        var details = evt.Details.Value;
        if (details.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        if (details.TryGetProperty("sv.message.id", out var topLevel)
            && topLevel.ValueKind == System.Text.Json.JsonValueKind.String
            && string.Equals(topLevel.GetString(), messageId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (details.TryGetProperty("messageId", out var camel)
            && camel.ValueKind == System.Text.Json.JsonValueKind.String
            && string.Equals(camel.GetString(), messageId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (details.TryGetProperty("sv.resource", out var resource)
            && resource.ValueKind == System.Text.Json.JsonValueKind.Object
            && resource.TryGetProperty("sv.message.id", out var nested)
            && nested.ValueKind == System.Text.Json.JsonValueKind.String
            && string.Equals(nested.GetString(), messageId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
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

        return source.SelectMany(evt =>
        {
            if (!evt.Source.Scheme.Equals("unit", StringComparison.OrdinalIgnoreCase))
            {
                return Observable.Return(evt);
            }

            // Fast path: permission already resolved for this unit path in this session.
            if (cache.TryGetValue(evt.Source.Path, out var cached))
            {
                return cached ? Observable.Return(evt) : Observable.Empty<ActivityEvent>();
            }

            // Slow path: first event from this unit path — resolve asynchronously so
            // the event-publisher thread is not blocked. Two concurrent first-events
            // for the same path may both call ResolveEffectivePermissionAsync; TryAdd
            // is idempotent and both yield the same deterministic result.
            return Observable.FromAsync(async () =>
                {
                    try
                    {
                        // Hierarchy-aware (#414): resolve the effective permission
                        // so a Viewer on an ancestor unit can observe events from
                        // descendant units without a direct grant on each one.
                        var permission = await permissionService
                            .ResolveEffectivePermissionAsync(humanId, evt.Source.Path, CancellationToken.None);
                        var allowed = permission.HasValue && permission.Value >= PermissionLevel.Viewer;
                        cache.TryAdd(evt.Source.Path, allowed);
                        return allowed;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Permission lookup failed for human {HumanId} on unit {UnitId}; denying.",
                            humanId, evt.Source.Path);
                        cache.TryAdd(evt.Source.Path, false);
                        return false;
                    }
                })
                .Where(allowed => allowed)
                .Select(_ => evt);
        });
    }
}
