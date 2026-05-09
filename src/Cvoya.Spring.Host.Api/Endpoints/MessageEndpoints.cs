// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps message-related API endpoints.
/// </summary>
public static class MessageEndpoints
{
    /// <summary>
    /// Registers message endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/messages")
            .WithTags("Messages");

        group.MapPost("/", SendMessageAsync)
            .WithName("SendMessage")
            .WithSummary("Send a message routed via the message router")
            .Produces<MessageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        // #1209: surface the message body so operators can see *what* was
        // said, not just that a message went by. Backs both the CLI's
        // `spring message show <id>` and the portal's per-message detail.
        group.MapGet("/{messageId:guid}", GetMessageAsync)
            .WithName("GetMessage")
            .WithSummary("Get a single message (envelope + body) by id")
            .Produces<MessageDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetMessageAsync(
        Guid messageId,
        IMessageQueryService messageQueryService,
        CancellationToken cancellationToken)
    {
        var detail = await messageQueryService.GetAsync(messageId, cancellationToken);
        if (detail is null)
        {
            return Results.Problem(
                detail: $"Message '{messageId}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }
        return Results.Ok(detail);
    }

    private static async Task<IResult> SendMessageAsync(
        SendMessageRequest request,
        IMessageRouter messageRouter,
        IAuthenticatedCallerAccessor callerAccessor,
        IThreadRegistry threadRegistry,
        CancellationToken cancellationToken)
    {
        // #339: Use the authenticated subject's identity as the From address
        // so MessageRouter's permission gate evaluates against the real
        // caller. Falls back to `human://api` only when no authenticated
        // principal is present (e.g. out-of-request contexts) — which matches
        // the pre-fix behaviour and the fallback used by UnitCreationService
        // for its creator grant (#328). The local-dev branch is no longer
        // needed: LocalDevAuthHandler surfaces the `local-dev-user`
        // NameIdentifier, and the caller accessor picks it up automatically.
        var from = await callerAccessor.GetCallerAddressAsync(cancellationToken);

        if (!Enum.TryParse<MessageType>(request.Type, ignoreCase: true, out var messageType))
        {
            return Results.Problem(detail: $"Invalid message type: '{request.Type}'", statusCode: StatusCodes.Status400BadRequest);
        }

        var to = Address.For(request.To.Scheme, request.To.Path);
        var messageId = Guid.NewGuid();

        // #2047 / ADR-0030: every Domain message must carry a participant-set
        // thread id so the activity-event CorrelationId is populated and
        // observability can stitch the timeline. Resolve the id through the
        // thread registry — same caller / destination set in any order
        // produces the same id, so follow-up sends thread under the same
        // conversation without the client tracking the value. Caller-supplied
        // ids are preserved as-is; control messages (HealthCheck / Cancel /
        // StatusQuery) skip the auto-resolve because they don't participate
        // in conversation.
        var threadId = request.ThreadId;
        if (messageType == MessageType.Domain)
        {
            if (string.IsNullOrWhiteSpace(threadId))
            {
                threadId = await threadRegistry.GetOrCreateAsync(
                    new[] { from, to }, cancellationToken);
            }
            else if (!GuidFormatter.TryParse(threadId, out _))
            {
                // #2047 / ADR-0030: thread ids are stable Guids. Lenient
                // parsing accepts both dashed and no-dash forms
                // (CONVENTIONS.md § Identifiers); anything else is a client
                // mistake — return 400 before the message is routed. Only
                // Domain sends pin to a thread, so control / amendment
                // messages pass through unchanged.
                return Results.Problem(
                    detail: $"ThreadId '{threadId}' is not a valid Guid.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var message = new Message(
            messageId,
            from,
            to,
            messageType,
            threadId,
            request.Payload,
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

        return Results.Ok(new MessageResponse(messageId, threadId, result.Value?.Payload));
    }
}
