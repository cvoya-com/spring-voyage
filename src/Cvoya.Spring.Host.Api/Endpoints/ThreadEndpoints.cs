// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

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
            .Produces<IReadOnlyList<ThreadSummary>>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetThreadAsync)
            .WithName("GetThread")
            .WithSummary("Get a single thread (summary + ordered events)")
            .Produces<ThreadDetail>(StatusCodes.Status200OK)
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
            .Produces<ThreadDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListThreadsAsync(
        [AsParameters] ThreadListQuery query,
        IThreadQueryService queryService,
        CancellationToken cancellationToken)
    {
        var filters = new ThreadQueryFilters(
            Unit: query.Unit,
            Agent: query.Agent,
            Status: query.Status,
            Participant: query.Participant,
            Limit: query.Limit);

        var summaries = await queryService.ListAsync(filters, cancellationToken);
        return Results.Ok(summaries);
    }

    private static async Task<IResult> GetThreadAsync(
        string id,
        IThreadQueryService queryService,
        CancellationToken cancellationToken)
    {
        var detail = await queryService.GetAsync(id, cancellationToken);
        if (detail is null)
        {
            return Results.Problem(
                detail: $"Thread '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(detail);
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

        return Results.Ok(new ThreadMessageResponse(messageId, id, result.Value?.Payload));
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
        return Results.Ok(updated);
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
            .Produces<IReadOnlyList<InboxItem>>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> ListInboxAsync(
        IThreadQueryService queryService,
        IAuthenticatedCallerAccessor callerAccessor,
        CancellationToken cancellationToken)
    {
        var caller = callerAccessor.GetHumanAddress();
        var items = await queryService.ListInboxAsync(
            $"{caller.Scheme}://{caller.Path}",
            cancellationToken);
        return Results.Ok(items);
    }
}