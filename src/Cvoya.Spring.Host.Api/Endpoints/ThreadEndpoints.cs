// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps the thread read + send endpoints introduced by #452. Threads
/// are a projection of the activity-event store — see
/// <see cref="IThreadQueryService"/> — so these endpoints stay thin: they
/// delegate reads to the query service and threaded sends to the existing
/// <see cref="IMessageRouter"/>, stamping the path's thread id onto the
/// outbound message.
/// </summary>
public static class ThreadEndpoints
{
    /// <summary>
    /// Registers thread endpoints on the supplied route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group for chaining.</returns>
    public static RouteGroupBuilder MapThreadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/threads")
            .WithTags("Threads");

        group.MapGet("/", ListThreadsAsync)
            .WithName("ListThreads")
            .WithSummary("List threads derived from the activity event stream")
            .Produces<IReadOnlyList<ThreadSummaryResponse>>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetThreadAsync)
            .WithName("GetThread")
            .WithSummary("Get a single thread (summary + ordered events)")
            .Produces<ThreadDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/messages", PostThreadMessageAsync)
            .WithName("PostThreadMessage")
            .WithSummary("Thread a new message into an existing thread")
            .Produces<ThreadMessageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        // #1038: explicit operator-driven close so a single failed dispatch
        // doesn't permanently brick an agent. We use `/{id}/close` rather
        // than `/{id}:close` so the route plays nicely with the existing
        // `/{id}/messages` sibling and routing template constraints; the
        // verb-style URL was tempting but inconsistent with the rest of the
        // surface.
        group.MapPost("/{id}/close", CloseThreadAsync)
            .WithName("CloseThread")
            .WithSummary("Close (abort) an in-flight or pending thread across all participating agents")
            .Produces<ThreadDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListThreadsAsync(
        [AsParameters] ThreadListQuery query,
        IThreadQueryService queryService,
        IParticipantDisplayNameResolver resolver,
        CancellationToken cancellationToken)
    {
        var filters = new ThreadQueryFilters(
            Unit: query.Unit,
            Agent: query.Agent,
            Status: query.Status,
            Participant: query.Participant,
            Limit: query.Limit);

        var summaries = await queryService.ListAsync(filters, cancellationToken);
        var enriched = await EnrichSummariesAsync(summaries, resolver, cancellationToken);
        return Results.Ok(enriched);
    }

    private static async Task<IResult> GetThreadAsync(
        string id,
        IThreadQueryService queryService,
        IParticipantDisplayNameResolver resolver,
        CancellationToken cancellationToken)
    {
        var detail = await queryService.GetAsync(id, cancellationToken);
        if (detail is null)
        {
            return Results.Problem(
                detail: $"Thread '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var enriched = await EnrichDetailAsync(detail, resolver, cancellationToken);
        return Results.Ok(enriched);
    }

    private static async Task<IResult> PostThreadMessageAsync(
        string id,
        ThreadMessageRequest request,
        IMessageRouter messageRouter,
        IAuthenticatedCallerAccessor callerAccessor,
        CancellationToken cancellationToken)
    {
        if (request is null || request.To is null || string.IsNullOrWhiteSpace(request.To.Scheme))
        {
            return Results.Problem(
                detail: "Request body must include a destination address (to.scheme and to.path).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Problem(
                detail: "Thread id is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var from = callerAccessor.GetHumanAddress();
        var to = new Address(request.To.Scheme, request.To.Path ?? string.Empty);
        var messageId = Guid.NewGuid();

        // Wrap the text as a Domain payload — same shape as SendMessage.
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(request.Text ?? string.Empty);
        var message = new Message(
            messageId,
            from,
            to,
            MessageType.Domain,
            id,
            payload,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(message, cancellationToken);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            return error.Code switch
            {
                "ADDRESS_NOT_FOUND" => Results.Problem(
                    detail: error.Detail ?? error.Message,
                    statusCode: StatusCodes.Status404NotFound),
                "PERMISSION_DENIED" => Results.Problem(
                    detail: error.Detail ?? error.Message,
                    statusCode: StatusCodes.Status403Forbidden),
                // #993: caller-side validation thrown by the destination
                // actor surfaces as 400 with a stable `code` extension so
                // clients can switch on it without parsing the message.
                "CALLER_VALIDATION" => Results.Problem(
                    title: "Bad Request",
                    detail: error.Detail ?? error.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = error.DetailCode,
                    }),
                _ => Results.Problem(
                    detail: error.Message,
                    statusCode: StatusCodes.Status502BadGateway),
            };
        }

        // Echo the kind back on the response so callers can confirm what
        // was accepted. Normalise to lower-case and default to "information"
        // when the caller omitted it.
        var kind = string.IsNullOrWhiteSpace(request.Kind)
            ? Models.MessageKind.Information
            : request.Kind.ToLowerInvariant();

        return Results.Ok(new ThreadMessageResponse(messageId, id, result.Value?.Payload, kind));
    }

    /// <summary>
    /// Closes (aborts) a thread across every agent participant, then
    /// returns the (now-closed) thread detail. See #1038 — without this
    /// surface a single failed dispatch leaves an agent permanently busy
    /// because the active-thread pointer is persisted in actor state.
    /// </summary>
    private static async Task<IResult> CloseThreadAsync(
        string id,
        CloseThreadRequest request,
        IThreadQueryService queryService,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IParticipantDisplayNameResolver resolver,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Problem(
                detail: "Thread id is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var detail = await queryService.GetAsync(id, cancellationToken);
        if (detail is null)
        {
            return Results.Problem(
                detail: $"Thread '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.ThreadEndpoints");
        var reason = request?.Reason;

        // Walk the participants on the summary, keeping only agent-scheme
        // entries (humans don't have actor proxies and units close as a
        // side-effect of their member agents closing). Any participant the
        // directory can't resolve is skipped with a structured warning rather
        // than failing the whole close — the operator's intent is "stop this
        // thread", and a missing participant shouldn't block the others.
        foreach (var participant in detail.Summary.Participants)
        {
            if (!TryParseAgentParticipant(participant, out var agentAddress))
            {
                continue;
            }

            DirectoryEntry? entry;
            try
            {
                entry = await directoryService.ResolveAsync(agentAddress, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to resolve participant {Participant} while closing thread {ThreadId}.",
                    participant, id);
                continue;
            }

            if (entry is null)
            {
                logger.LogWarning(
                    "Participant {Participant} not found in directory while closing thread {ThreadId}.",
                    participant, id);
                continue;
            }

            try
            {
                var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                    new ActorId(entry.ActorId), nameof(AgentActor));
                await proxy.CloseConversationAsync(id, reason, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "CloseConversationAsync failed on participant {Participant} for thread {ThreadId}.",
                    participant, id);
            }
        }

        // Re-read the detail so the response reflects the close events the
        // actors just emitted (the read model is event-sourced — by the time
        // we return, the ThreadClosed events should be projected).
        var updated = await queryService.GetAsync(id, cancellationToken) ?? detail;
        var enriched = await EnrichDetailAsync(updated, resolver, cancellationToken);
        return Results.Ok(enriched);
    }

    // ---------------------------------------------------------------------------
    // Enrichment helpers
    // ---------------------------------------------------------------------------

    internal static async Task<ParticipantRef> ToRefAsync(
        string address,
        IParticipantDisplayNameResolver resolver,
        CancellationToken ct)
    {
        var displayName = await resolver.ResolveAsync(address, ct);
        return new ParticipantRef(address, displayName);
    }

    internal static async Task<IReadOnlyList<ThreadSummaryResponse>> EnrichSummariesAsync(
        IReadOnlyList<Cvoya.Spring.Core.Observability.ThreadSummary> summaries,
        IParticipantDisplayNameResolver resolver,
        CancellationToken ct)
    {
        var result = new List<ThreadSummaryResponse>(summaries.Count);
        foreach (var s in summaries)
        {
            result.Add(await EnrichSummaryAsync(s, resolver, ct));
        }
        return result;
    }

    internal static async Task<ThreadSummaryResponse> EnrichSummaryAsync(
        Cvoya.Spring.Core.Observability.ThreadSummary s,
        IParticipantDisplayNameResolver resolver,
        CancellationToken ct)
    {
        var participants = new List<ParticipantRef>(s.Participants.Count);
        foreach (var p in s.Participants)
        {
            participants.Add(await ToRefAsync(p, resolver, ct));
        }
        var origin = await ToRefAsync(s.Origin, resolver, ct);
        return new ThreadSummaryResponse(
            s.Id,
            participants,
            s.Status,
            s.LastActivity,
            s.CreatedAt,
            s.EventCount,
            origin,
            s.Summary);
    }

    internal static async Task<ThreadDetailResponse> EnrichDetailAsync(
        Cvoya.Spring.Core.Observability.ThreadDetail detail,
        IParticipantDisplayNameResolver resolver,
        CancellationToken ct)
    {
        var summary = await EnrichSummaryAsync(detail.Summary, resolver, ct);
        var events = new List<ThreadEventResponse>(detail.Events.Count);
        foreach (var e in detail.Events)
        {
            var source = await ToRefAsync(e.Source, resolver, ct);
            ParticipantRef? from = e.From is not null
                ? await ToRefAsync(e.From, resolver, ct)
                : null;
            events.Add(new ThreadEventResponse(
                e.Id,
                e.Timestamp,
                source,
                e.EventType,
                e.Severity,
                e.Summary,
                e.MessageId,
                from,
                e.To,
                e.Body));
        }
        return new ThreadDetailResponse(summary, events);
    }

    private static bool TryParseAgentParticipant(string participant, out Address address)
    {
        address = default!;
        if (string.IsNullOrWhiteSpace(participant))
        {
            return false;
        }

        var separatorIdx = participant.IndexOf("://", StringComparison.Ordinal);
        string scheme;
        string path;
        if (separatorIdx > 0)
        {
            scheme = participant[..separatorIdx];
            path = participant[(separatorIdx + 3)..];
        }
        else
        {
            var colonIdx = participant.IndexOf(':');
            if (colonIdx <= 0)
            {
                return false;
            }
            scheme = participant[..colonIdx];
            path = participant[(colonIdx + 1)..];
        }

        if (!string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        address = new Address(scheme, path);
        return true;
    }
}

/// <summary>
/// Maps the inbox endpoint introduced by #456. The inbox is a filtered view of
/// <see cref="IThreadQueryService.ListInboxAsync"/> scoped to the
/// authenticated caller's <c>human://</c> address. "Respond" is explicitly not
/// a separate endpoint: it's a thin wrapper over
/// <see cref="ThreadEndpoints.MapThreadEndpoints"/> — the
/// <c>POST /api/v1/threads/{id}/messages</c> call — so we don't fork the
/// message-send contract.
///
/// #1477 adds <c>POST /api/v1/tenant/inbox/{threadId}/mark-read</c> which
/// writes a per-thread read cursor on the caller's <see cref="IHumanActor"/>
/// so subsequent <c>GET /api/v1/tenant/inbox</c> calls populate
/// <see cref="InboxItem.UnreadCount"/> accurately.
/// </summary>
public static class InboxEndpoints
{
    /// <summary>
    /// Registers inbox endpoints on the supplied route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group for chaining.</returns>
    public static RouteGroupBuilder MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/inbox")
            .WithTags("Inbox");

        group.MapGet("/", ListInboxAsync)
            .WithName("ListInbox")
            .WithSummary("List threads awaiting the current human caller")
            .Produces<IReadOnlyList<InboxItemResponse>>(StatusCodes.Status200OK);

        group.MapPost("/{threadId}/mark-read", MarkReadAsync)
            .WithName("MarkInboxThreadRead")
            .WithSummary("Record that the current human has read the specified inbox thread")
            .Produces<InboxItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListInboxAsync(
        IThreadQueryService queryService,
        IAuthenticatedCallerAccessor callerAccessor,
        IActorProxyFactory actorProxyFactory,
        IParticipantDisplayNameResolver resolver,
        CancellationToken cancellationToken)
    {
        var caller = callerAccessor.GetHumanAddress();
        var callerAddress = $"{caller.Scheme}://{caller.Path}";

        // Fetch the caller's per-thread read cursors from their HumanActor so
        // the query service can compute UnreadCount per row.
        IReadOnlyDictionary<string, DateTimeOffset>? lastReadAt = null;
        try
        {
            var humanProxy = actorProxyFactory.CreateActorProxy<IHumanActor>(
                new ActorId(caller.Path), nameof(HumanActor));
            var entries = await humanProxy.GetLastReadAtAsync(cancellationToken);
            lastReadAt = entries.ToDictionary(e => e.ThreadId, e => e.LastReadAt);
        }
        catch
        {
            // Actor unavailable — proceed without unread data; all items get UnreadCount=0.
        }

        var items = await queryService.ListInboxAsync(callerAddress, lastReadAt, cancellationToken);
        var enriched = await EnrichInboxItemsAsync(items, resolver, cancellationToken);
        return Results.Ok(enriched);
    }

    /// <summary>
    /// Records <c>now</c> as the read cursor for <paramref name="threadId"/>
    /// on the caller's <see cref="IHumanActor"/>. Idempotent — repeated calls
    /// only advance the cursor. Returns the updated <see cref="InboxItemResponse"/> so
    /// the portal can reconcile the cache in one round-trip.
    /// </summary>
    private static async Task<IResult> MarkReadAsync(
        string threadId,
        IAuthenticatedCallerAccessor callerAccessor,
        IThreadQueryService queryService,
        IActorProxyFactory actorProxyFactory,
        IParticipantDisplayNameResolver resolver,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return Results.Problem(
                detail: "Thread id is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var caller = callerAccessor.GetHumanAddress();
        var readAt = DateTimeOffset.UtcNow;

        var humanProxy = actorProxyFactory.CreateActorProxy<IHumanActor>(
            new ActorId(caller.Path), nameof(HumanActor));

        await humanProxy.MarkReadAsync(threadId, readAt, cancellationToken);

        // Return the refreshed inbox item (UnreadCount should now be 0).
        var entries = await humanProxy.GetLastReadAtAsync(cancellationToken);
        var lastReadAt = entries.ToDictionary(e => e.ThreadId, e => e.LastReadAt);
        var callerAddress = $"{caller.Scheme}://{caller.Path}";
        var items = await queryService.ListInboxAsync(callerAddress, lastReadAt, cancellationToken);
        var rawUpdated = items.FirstOrDefault(i => string.Equals(i.ThreadId, threadId, StringComparison.Ordinal));

        if (rawUpdated is null)
        {
            return Results.Problem(
                detail: $"Thread '{threadId}' not found in inbox.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var updated = await EnrichInboxItemAsync(rawUpdated, resolver, cancellationToken);
        return Results.Ok(updated);
    }

    // ---------------------------------------------------------------------------
    // Inbox enrichment helpers
    // ---------------------------------------------------------------------------

    private static async Task<IReadOnlyList<InboxItemResponse>> EnrichInboxItemsAsync(
        IReadOnlyList<Cvoya.Spring.Core.Observability.InboxItem> items,
        IParticipantDisplayNameResolver resolver,
        CancellationToken ct)
    {
        var result = new List<InboxItemResponse>(items.Count);
        foreach (var item in items)
        {
            result.Add(await EnrichInboxItemAsync(item, resolver, ct));
        }
        return result;
    }

    private static async Task<InboxItemResponse> EnrichInboxItemAsync(
        Cvoya.Spring.Core.Observability.InboxItem item,
        IParticipantDisplayNameResolver resolver,
        CancellationToken ct)
    {
        var from = await ThreadEndpoints.ToRefAsync(item.From, resolver, ct);
        var human = await ThreadEndpoints.ToRefAsync(item.Human, resolver, ct);
        return new InboxItemResponse(
            item.ThreadId,
            from,
            human,
            item.PendingSince,
            item.Summary,
            item.UnreadCount);
    }
}