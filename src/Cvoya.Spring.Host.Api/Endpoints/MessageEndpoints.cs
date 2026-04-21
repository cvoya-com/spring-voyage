// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Messaging;
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
        var group = app.MapGroup("/api/v1/messages")
            .WithTags("Messages");

        group.MapPost("/", SendMessageAsync)
            .WithName("SendMessage")
            .WithSummary("Send a message routed via the message router")
            .Produces<MessageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return group;
    }

    private static async Task<IResult> SendMessageAsync(
        SendMessageRequest request,
        IMessageRouter messageRouter,
        IAuthenticatedCallerAccessor callerAccessor,
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
        var from = callerAccessor.GetHumanAddress();

        if (!Enum.TryParse<MessageType>(request.Type, ignoreCase: true, out var messageType))
        {
            return Results.Problem(detail: $"Invalid message type: '{request.Type}'", statusCode: StatusCodes.Status400BadRequest);
        }

        var to = new Address(request.To.Scheme, request.To.Path);
        var messageId = Guid.NewGuid();

        // #985: AgentActor.HandleDomainMessageAsync hard-requires a
        // ConversationId on Domain messages and surfaces the raw exception as
        // a 502 when it's missing. The OpenAPI contract marks the field
        // optional, so callers following the schema verbatim hit that
        // footgun. Mirror what the unit-routed path effectively does and
        // auto-generate a conversation id for Domain messages bound for an
        // agent:// target when the caller didn't supply one. The generated
        // (or caller-supplied) id is surfaced back on MessageResponse so the
        // caller can thread follow-up sends under the same conversation.
        var conversationId = request.ConversationId;
        if (messageType == MessageType.Domain
            && string.Equals(to.Scheme, "agent", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(conversationId))
        {
            conversationId = Guid.NewGuid().ToString();
        }

        var message = new Message(
            messageId,
            from,
            to,
            messageType,
            conversationId,
            request.Payload,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(message, cancellationToken);

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            var statusCode = error.Code switch
            {
                "ADDRESS_NOT_FOUND" => StatusCodes.Status404NotFound,
                "PERMISSION_DENIED" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status502BadGateway
            };

            return Results.Problem(
                detail: error.Message,
                statusCode: statusCode);
        }

        return Results.Ok(new MessageResponse(messageId, conversationId, result.Value?.Payload));
    }
}