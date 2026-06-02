// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps message-related API endpoints.
/// </summary>
public static class MessageEndpoints
{
    /// <summary>
    /// Stable, machine-readable code surfaced on the RFC-7807 <c>code</c>
    /// extension when a caller pins a Guid-shaped thread id whose canonical
    /// participant set excludes the resolved <c>Message.From</c> (#2865 /
    /// ADR-0030). Shared with <see cref="ThreadEndpoints"/>.
    /// </summary>
    public const string SenderNotInThreadCode = "SenderNotInThread";

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
        ITenantUserHumanResolver tenantUserHumanResolver,
        IActivityEventBus activityEventBus,
        IDirectoryService directoryService,
        IPermissionService permissionService,
        CancellationToken cancellationToken)
    {
        // #339: Use the authenticated subject's identity as the From address
        // so MessageRouter's permission gate evaluates against the real
        // caller. The endpoint is behind RequireAuthorization(TenantUser),
        // so a null caller here means the auth pipeline accepted the
        // request but did not surface a NameIdentifier claim — surface that
        // as a structured 401 rather than fabricating a synthetic identity
        // (#2405; ADR-0036 removed the non-Guid `human://api` fallback).
        var callerAddress = await callerAccessor.GetCallerAddressAsync(cancellationToken);
        if (callerAddress is null)
        {
            return Results.Problem(
                detail: "No authenticated caller identity available.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!Enum.TryParse<MessageType>(request.Type, ignoreCase: true, out var messageType))
        {
            return Results.Problem(detail: $"Invalid message type: '{request.Type}'", statusCode: StatusCodes.Status400BadRequest);
        }

        // #2887 — resolve the recipient set: exactly one of a single `To`
        // (a 1-1 send or a reply on an existing thread) or `Recipients[]`
        // (a multi-party send). A multi-party send resolves ONE shared thread
        // from {sender} ∪ recipients below, mirroring sv.messaging.send, so
        // every recipient lands on the same conversation instead of forking
        // into per-recipient threads.
        List<Address> recipients;
        if (request.Recipients is { Count: > 0 })
        {
            if (request.To is not null)
            {
                return Results.Problem(
                    detail: "Supply exactly one of 'to' or 'recipients', not both.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            recipients = new List<Address>(request.Recipients.Count);
            foreach (var recipient in request.Recipients)
            {
                recipients.Add(Address.For(recipient.Scheme, recipient.Path));
            }
        }
        else if (request.To is not null)
        {
            recipients = new List<Address> { Address.For(request.To.Scheme, request.To.Path) };
        }
        else
        {
            return Results.Problem(
                detail: "Supply exactly one of 'to' or 'recipients'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var messageId = Guid.NewGuid();

        // ADR-0062 § 3: Message.From must be a routable scheme
        // (agent / unit / human). The auth principal is tenant-user://,
        // which is non-routable. Resolve to the speaking-as Hat at the
        // API boundary so every downstream consumer (routing, directory,
        // agent-facing tool surface, portal render) sees a uniform
        // human://<id> sender. Control messages skip the rewrite — they
        // never carry a domain From; the caller principal travels in the
        // audit envelope instead.
        Address from;
        Guid? resolvedThreadGuid = null;
        if (messageType == MessageType.Domain
            && string.Equals(callerAddress.Scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(request.ThreadId)
                && GuidFormatter.TryParse(request.ThreadId, out var parsedThreadId))
            {
                resolvedThreadGuid = parsedThreadId;
            }

            try
            {
                from = await tenantUserHumanResolver.PickFromAsync(
                    callerAddress.Id,
                    request.From,
                    resolvedThreadGuid,
                    cancellationToken);
            }
            catch (NoBoundHumanException ex)
            {
                return Results.Problem(
                    title: "Bad Request",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = ITenantUserHumanResolver.NoBoundHumanCode,
                    });
            }
        }
        else
        {
            from = callerAddress;
        }

        // The recipient set is a set: drop the sender (a human listing
        // themselves) and any duplicates so the resolved thread and the
        // fan-out match the caller's intent (mirrors HandleSendAsync's
        // recipient dedupe).
        var fromKey = from.ToString();
        var seenRecipients = new HashSet<string>(StringComparer.Ordinal);
        var dedupedRecipients = new List<Address>(recipients.Count);
        foreach (var recipient in recipients)
        {
            var key = recipient.ToString();
            if (string.Equals(key, fromKey, StringComparison.Ordinal))
            {
                continue;
            }
            if (seenRecipients.Add(key))
            {
                dedupedRecipients.Add(recipient);
            }
        }
        recipients = dedupedRecipients;
        if (recipients.Count == 0)
        {
            return Results.Problem(
                detail: "The message has no recipients other than the sender.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // #2859: run the unit-permission gate for EVERY unit recipient BEFORE
        // any persistence side-effect — thread-registry creation,
        // activity-event emit, message envelope write. A forbidden send must
        // leave the database completely clean (no `messages` row, no new
        // `threads` row, no Activity event) so the Conversations /
        // Engagements pages and the thread timeline don't surface phantom
        // sends the recipient never received. The router runs its own gate
        // (defence in depth for non-endpoint callers like connectors), but
        // the persist sites above the router live in this handler, so the
        // gate is hoisted here.
        //
        // Only Domain messages routed to unit:// destinations from a
        // permission-bearing principal (human:// or tenant-user://) need
        // the precheck. Control messages (HealthCheck / Cancel /
        // StatusQuery) and routings to other schemes pass through.
        if (messageType == MessageType.Domain
            && (string.Equals(from.Scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase)
                || string.Equals(from.Scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var recipient in recipients)
            {
                if (!string.Equals(recipient.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var unitEntry = await directoryService.ResolveAsync(recipient, cancellationToken);
                if (unitEntry is null)
                {
                    return Results.Problem(
                        detail: $"Address '{recipient.Scheme}://{recipient.Path}' not found",
                        statusCode: StatusCodes.Status404NotFound);
                }

                var permission = await permissionService.ResolveEffectivePermissionAsync(
                    from, unitEntry.ActorId, cancellationToken);
                if (permission is null || (int)permission.Value < (int)PermissionLevel.Viewer)
                {
                    // Match the router's PERMISSION_DENIED surface so the
                    // operator-visible 403 message is uniform regardless of
                    // which gate (endpoint precheck or router) caught it.
                    return Results.Problem(
                        detail: $"Permission denied for address {recipient.Scheme}://{recipient.Path}",
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }
        }

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
                // #2887 — one shared thread for the whole send, keyed on the
                // full participant set {sender} ∪ recipients. For a 1-1 send
                // this is {from, to}; for a multi-party send every recipient
                // resolves to the same thread so the conversation does not
                // fork.
                var participantSet = new List<Address>(recipients.Count + 1) { from };
                participantSet.AddRange(recipients);
                threadId = await threadRegistry.GetOrCreateAsync(
                    participantSet, cancellationToken);
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
            else
            {
                // #2865 / ADR-0030: when the caller pins a Guid-shaped
                // thread id, the resolved sender MUST be a canonical
                // participant of that thread. Without this gate, the
                // endpoint silently accepts the send, then the reply re-
                // routes through `sv.messaging.send` onto the canonical
                // {sender, recipient} thread and the conversation splits
                // across two `spring.threads` rows. Unknown ids continue
                // to pass through as caller-supplied stable correlation
                // ids (#2112). The gate fires before audit emit + router
                // so a 400 leaves the DB clean — same rule as #2859.
                var existing = await threadRegistry.ResolveAsync(threadId, cancellationToken);
                if (existing is not null && !ParticipantsContain(existing.Participants, from))
                {
                    return Results.Problem(
                        title: "Bad Request",
                        detail: $"Sender '{from.Scheme}://{GuidFormatter.Format(from.Id)}' is not a participant of thread '{threadId}'.",
                        statusCode: StatusCodes.Status400BadRequest,
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = SenderNotInThreadCode,
                        });
                }
            }
        }

        // Fan out one logical message (a single Message.Id) to every
        // recipient on the shared thread (#2887). Mirrors sv.messaging.send:
        // the persist is idempotent on Message.Id so a single row backs the
        // conversation, and each RouteAsync delivers to one recipient's actor
        // on the same thread. The audit envelope (ADR-0062 § 4) is emitted
        // once for the logical send.
        var auditEmitted = false;
        Message? routedValue = null;
        foreach (var recipient in recipients)
        {
            var message = new Message(
                messageId,
                from,
                recipient,
                messageType,
                threadId,
                request.Payload,
                DateTimeOffset.UtcNow);

            // ADR-0062 § 4: dual-stamp the audit envelope so the auth
            // principal (the TenantUser that drove the send) is
            // reconstructible from observation alone, alongside the routable
            // `human://` From on the message. Best-effort: a publish failure
            // must not block the send.
            if (!auditEmitted)
            {
                await OutboundMessageAuditEmitter.EmitAsync(
                    activityEventBus,
                    message,
                    callerAddress,
                    cancellationToken);
                auditEmitted = true;
            }

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
                    // #2981: the destination unit/agent is stopped. 409 Conflict
                    // — the target exists but is not in a state that accepts
                    // messages; the caller must start it first.
                    "RECIPIENT_STOPPED" => Results.Problem(
                        title: "Recipient stopped",
                        detail: error.Detail ?? error.Message,
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "RecipientStopped",
                        }),
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

            routedValue = result.Value;
        }

        return Results.Ok(new MessageResponse(messageId, threadId, routedValue?.Payload));
    }

    /// <summary>
    /// Returns true when <paramref name="from"/> is one of the canonical
    /// participants in <paramref name="participants"/>. The scheme
    /// comparison is case-insensitive — the registry stores schemes
    /// lower-cased on canonicalisation, but a caller-constructed
    /// <see cref="Address"/> may use the constant's original case, so
    /// equality cannot depend on case.
    /// </summary>
    internal static bool ParticipantsContain(IReadOnlyList<Address> participants, Address from)
    {
        for (var i = 0; i < participants.Count; i++)
        {
            var p = participants[i];
            if (p.Id == from.Id
                && string.Equals(p.Scheme, from.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
