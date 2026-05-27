// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        return group;
    }

    private static async Task<IResult> ListThreadsAsync(
        [AsParameters] ThreadListQuery query,
        IThreadQueryService queryService,
        IParticipantDisplayNameResolver resolver,
        IAuthenticatedCallerAccessor callerAccessor,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var filters = new ThreadQueryFilters(
            Unit: query.Unit,
            Agent: query.Agent,
            Participant: query.Participant,
            Limit: query.Limit,
            Archived: query.Archived);

        var summaries = await queryService.ListAsync(filters, cancellationToken);
        // ADR-0062 § 5 / #2829: resolve the disambiguated Hat labels for
        // the recipient column once per page against the caller's
        // bound-Hats set. The same lookup powers both the chip rendering
        // and the inbox-toolbar filter (the latter sources its options
        // from /me/humans directly, so the strings match).
        var recipientLabels = await BuildRecipientHumanLabelsAsync(
            callerAccessor, db, cancellationToken);
        var enriched = await EnrichSummariesAsync(summaries, resolver, recipientLabels, cancellationToken);
        return Results.Ok(enriched);
    }

    private static async Task<IResult> GetThreadAsync(
        string id,
        IThreadQueryService queryService,
        IParticipantDisplayNameResolver resolver,
        IAuthenticatedCallerAccessor callerAccessor,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var detail = await queryService.GetAsync(id, cancellationToken);
        if (detail is null)
        {
            return Results.Problem(
                detail: $"Thread '{id}' not found",
                statusCode: StatusCodes.Status404NotFound);
        }

        var recipientLabels = await BuildRecipientHumanLabelsAsync(
            callerAccessor, db, cancellationToken);
        var enriched = await EnrichDetailAsync(detail, resolver, recipientLabels, cancellationToken);
        return Results.Ok(enriched);
    }

    /// <summary>
    /// Resolves the calling caller's bound-Hats set and computes the
    /// disambiguated label for each (ADR-0062 § 5 / #2829). Returns a
    /// <c>HumanId → disambiguatedLabel</c> map used by
    /// <see cref="EnrichSummaryAsync"/> to stamp
    /// <c>ThreadSummaryResponse.RecipientHumanDisambiguatedLabel</c>.
    /// Returns an empty map for a non-TenantUser caller or when the
    /// caller has no bound Hats — both cases collapse to "render the
    /// raw display name without disambiguation" on the chip surface.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, string>> BuildRecipientHumanLabelsAsync(
        IAuthenticatedCallerAccessor callerAccessor,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var callerAddress = await callerAccessor.GetCallerAddressAsync(cancellationToken);
        if (callerAddress is null
            || !string.Equals(callerAddress.Scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<Guid, string>();
        }

        var callerTenantUserId = callerAddress.Id;

        // Pull the caller's bound Humans and the first-membership data
        // each Hat carries. Same shape as TenantUserIdentityEndpoints
        // ListCallerHumansAsync — the from-selector and the chip
        // surface read the same set so the labels match across
        // surfaces.
        var humans = await db.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == callerTenantUserId)
            .Select(h => new { h.Id, h.Username, h.DisplayName })
            .ToListAsync(cancellationToken);

        if (humans.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var humanIds = humans.Select(h => h.Id).ToList();

        var memberships = await (
            from m in db.UnitMembershipsHumans.AsNoTracking()
            where humanIds.Contains(m.HumanId)
            join u in db.UnitDefinitions.AsNoTracking() on m.UnitId equals u.Id into uj
            from u in uj.DefaultIfEmpty()
            select new
            {
                m.HumanId,
                m.UnitId,
                UnitDisplayName = u != null ? u.DisplayName : string.Empty,
                m.Roles,
            }).ToListAsync(cancellationToken);

        var firstMembershipByHuman = memberships
            .GroupBy(m => m.HumanId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(x => x.UnitDisplayName, StringComparer.OrdinalIgnoreCase)
                    .First());

        var candidates = humans
            .Select(h =>
            {
                firstMembershipByHuman.TryGetValue(h.Id, out var first);
                return new HatLabelCandidate(
                    HumanId: h.Id,
                    BaseName: string.IsNullOrWhiteSpace(h.DisplayName) ? h.Username : h.DisplayName,
                    UnitDisplayName: first?.UnitDisplayName,
                    Roles: first?.Roles);
            })
            .ToList();

        return HatLabelDisambiguator.DisambiguateAll(candidates);
    }

    private static async Task<IResult> PostThreadMessageAsync(
        string id,
        ThreadMessageRequest request,
        IMessageRouter messageRouter,
        IAuthenticatedCallerAccessor callerAccessor,
        ITenantUserHumanResolver tenantUserHumanResolver,
        IActivityEventBus activityEventBus,
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

        var callerAddress = await callerAccessor.GetCallerAddressAsync(cancellationToken);
        if (callerAddress is null)
        {
            // Endpoint sits behind RequireAuthorization(TenantUser); a null
            // caller means the auth pipeline accepted the request but did
            // not surface a NameIdentifier claim. Surface as 401 rather
            // than fabricating a synthetic sender (#2405).
            return Results.Problem(
                detail: "No authenticated caller identity available.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // ADR-0062 § 3: rewrite the auth principal to the speaking-as Hat
        // before constructing the outbound. The thread id from the path
        // pins the reply default (the Hat that received the inbound on
        // this thread), with optional explicit From override.
        Address from;
        Guid? threadGuid = null;
        if (string.Equals(callerAddress.Scheme, Address.TenantUserScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (GuidFormatter.TryParse(id, out var parsedThreadId))
            {
                threadGuid = parsedThreadId;
            }

            try
            {
                from = await tenantUserHumanResolver.PickFromAsync(
                    callerAddress.Id,
                    request.From,
                    threadGuid,
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

        var to = Address.For(request.To.Scheme, request.To.Path ?? string.Empty);
        var messageId = Guid.NewGuid();

        // Wrap the text as a Domain payload — same shape as SendMessage.
        var payload = JsonSerializer.SerializeToElement(request.Text ?? string.Empty);
        var message = new Message(
            messageId,
            from,
            to,
            MessageType.Domain,
            id,
            payload,
            DateTimeOffset.UtcNow);

        // ADR-0062 § 4: dual-stamp the audit envelope.
        await OutboundMessageAuditEmitter.EmitAsync(
            activityEventBus,
            message,
            callerAddress,
            cancellationToken);

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

    // ---------------------------------------------------------------------------
    // Enrichment helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="address"/> to its display name, preferring
    /// a snapshot from <paramref name="snapshots"/> when the live resolver
    /// returns a per-scheme generic fallback (#2533). Snapshots capture
    /// the last-known real name a participant had at message-write time,
    /// so a later soft-delete of the underlying definition does not
    /// blank the engagement list to "an agent" / "a connector" — the
    /// human still sees the name they conversed with.
    /// </summary>
    internal static async Task<ParticipantRef> ToRefAsync(
        string address,
        IParticipantDisplayNameResolver resolver,
        IReadOnlyDictionary<string, string>? snapshots,
        CancellationToken ct)
    {
        var status = await resolver.ResolveStatusAsync(address, ct);
        var displayName = status.DisplayName;

        if (status.IsFallback
            && snapshots is not null
            && snapshots.TryGetValue(address, out var snapshot)
            && !string.IsNullOrWhiteSpace(snapshot))
        {
            displayName = snapshot;
        }

        // #2082: emit the typed Guid identity alongside the textual
        // address. Callers do identity comparisons on Id, not Address.
        // Slug-shaped legacy addresses (no Guid) surface Guid.Empty —
        // those rows can't participate in identity equality anyway.
        _ = AddressIdentity.TryGetActorId(address, out var id);
        return new ParticipantRef(id, address, displayName);
    }

    internal static async Task<IReadOnlyList<ThreadSummaryResponse>> EnrichSummariesAsync(
        IReadOnlyList<Cvoya.Spring.Core.Observability.ThreadSummary> summaries,
        IParticipantDisplayNameResolver resolver,
        IReadOnlyDictionary<Guid, string> recipientHumanLabels,
        CancellationToken ct)
    {
        var result = new List<ThreadSummaryResponse>(summaries.Count);
        foreach (var s in summaries)
        {
            result.Add(await EnrichSummaryAsync(s, resolver, recipientHumanLabels, ct));
        }
        return result;
    }

    internal static async Task<ThreadSummaryResponse> EnrichSummaryAsync(
        Cvoya.Spring.Core.Observability.ThreadSummary s,
        IParticipantDisplayNameResolver resolver,
        IReadOnlyDictionary<Guid, string> recipientHumanLabels,
        CancellationToken ct)
    {
        var snapshots = s.ParticipantNameSnapshots;
        var participants = new List<ParticipantRef>(s.Participants.Count);
        foreach (var p in s.Participants)
        {
            participants.Add(await ToRefAsync(p, resolver, snapshots, ct));
        }
        var origin = await ToRefAsync(s.Origin, resolver, snapshots, ct);

        // ADR-0062 § 5 (#2826): resolve the recipient Hat's display name
        // through the same snapshot-aware path the participant labels
        // use so the chip on the engagement list / messaging-tab matches
        // the name the rest of the row already shows. `s.RecipientHumanId`
        // is a typed Guid; render it through the canonical wire form so
        // the resolver picks the right row.
        Guid? recipientHumanId = null;
        string? recipientHumanDisplayName = null;
        string? recipientHumanDisambiguatedLabel = null;
        if (s.RecipientHumanId is Guid humanId)
        {
            recipientHumanId = humanId;
            var canonical = $"{Address.HumanScheme}:{GuidFormatter.Format(humanId)}";
            var hatRef = await ToRefAsync(canonical, resolver, snapshots, ct);
            recipientHumanDisplayName = hatRef.DisplayName;
            // ADR-0062 § 5 / #2829: stamp the disambiguated label when
            // the recipient Hat is in the caller's bound set. Outside
            // that scope (a Hat the caller isn't bound to) the chip
            // falls back to the raw display name; there is no
            // operator-facing surface that disambiguates names beyond
            // the caller's own Hats.
            if (recipientHumanLabels.TryGetValue(humanId, out var label))
            {
                recipientHumanDisambiguatedLabel = label;
            }
        }

        return new ThreadSummaryResponse(
            s.Id,
            participants,
            s.LastActivity,
            s.CreatedAt,
            s.EventCount,
            origin,
            s.Summary,
            s.IsArchived,
            recipientHumanId,
            recipientHumanDisplayName,
            recipientHumanDisambiguatedLabel);
    }

    internal static async Task<ThreadDetailResponse> EnrichDetailAsync(
        Cvoya.Spring.Core.Observability.ThreadDetail detail,
        IParticipantDisplayNameResolver resolver,
        IReadOnlyDictionary<Guid, string> recipientHumanLabels,
        CancellationToken ct)
    {
        var summary = await EnrichSummaryAsync(detail.Summary, resolver, recipientHumanLabels, ct);
        var snapshots = detail.Summary.ParticipantNameSnapshots;
        var events = new List<ThreadEventResponse>(detail.Events.Count);
        foreach (var e in detail.Events)
        {
            var source = await ToRefAsync(e.Source, resolver, snapshots, ct);
            ParticipantRef? from = e.From is not null
                ? await ToRefAsync(e.From, resolver, snapshots, ct)
                : null;
            // #1635: also enrich the recipient address so the portal
            // gets a non-empty display name without resolving the
            // address client-side.
            ParticipantRef? to = e.To is not null
                ? await ToRefAsync(e.To, resolver, snapshots, ct)
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
                to,
                e.Body));
        }
        return new ThreadDetailResponse(summary, events);
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
        IInboxIdentityResolver inboxIdentityResolver,
        IActorProxyFactory actorProxyFactory,
        IParticipantDisplayNameResolver resolver,
        CancellationToken cancellationToken)
    {
        var caller = await callerAccessor.GetCallerAddressAsync(cancellationToken);
        if (caller is null)
        {
            // Inbox is scoped to the authenticated caller. Without an
            // identity there is no inbox to list (#2405).
            return Results.Problem(
                detail: "No authenticated caller identity available.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // #2766: resolve the calling TenantUser to the set of HumanEntity
        // ids the inbox query should match against. OSS-default returns
        // every Human in the tenant; cloud overlay walks the explicit
        // Human → TenantUser mapping table.
        var humanIds = await inboxIdentityResolver.ResolveHumanIdsAsync(caller, cancellationToken);
        if (humanIds.Count == 0)
        {
            return Results.Ok(Array.Empty<InboxItemResponse>());
        }

        // Merge per-HumanActor read cursors. Each mapped Human has its
        // own HumanActor with its own cursor; the merged map biases
        // toward "fewer unread" (latest cursor per thread wins) so the
        // operator does not see inflated unread counts across the fan-in
        // set.
        var lastReadAt = await LoadMergedReadCursorsAsync(humanIds, actorProxyFactory, cancellationToken);

        var items = await queryService.ListInboxAsync(humanIds, lastReadAt, cancellationToken);
        var enriched = await EnrichInboxItemsAsync(items, resolver, cancellationToken);
        return Results.Ok(enriched);
    }

    /// <summary>
    /// Reads per-thread read cursors from every <see cref="HumanActor"/>
    /// in the supplied set and merges them with "most-recent cursor wins"
    /// per thread id. The merged cursor is the conservative bound used by
    /// the inbox query's unread computation — a thread is unread if
    /// <em>any</em> of the caller's mapped humans has unseen messages on
    /// it, so picking the most-recent cursor per thread biases toward
    /// "fewer unread" rather than overcounting.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>?> LoadMergedReadCursorsAsync(
        IReadOnlyCollection<Guid> humanIds,
        IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DateTimeOffset>? merged = null;
        foreach (var humanId in humanIds)
        {
            try
            {
                var humanProxy = actorProxyFactory.CreateActorProxy<IHumanActor>(
                    new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(humanId)),
                    nameof(HumanActor));
                var entries = await humanProxy.GetLastReadAtAsync(cancellationToken);
                if (entries.Length == 0)
                {
                    continue;
                }

                merged ??= new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (!merged.TryGetValue(entry.ThreadId, out var existing) || entry.LastReadAt > existing)
                    {
                        merged[entry.ThreadId] = entry.LastReadAt;
                    }
                }
            }
            catch
            {
                // Per-human actor unavailable — continue with the rest.
                // The query service tolerates a null overall cursor; a
                // partial map just means the unaffected humans' cursors
                // still apply.
            }
        }

        return merged;
    }

    /// <summary>
    /// Records <c>now</c> as the read cursor for <paramref name="threadId"/>
    /// on the <see cref="HumanActor"/> backing the specific recipient of
    /// the latest pending message on the thread (#2766). Pre-fix this
    /// advanced the caller's login HumanActor cursor — which was the
    /// wrong row when the inbox-pending entry was addressed to a
    /// different mapped Human (e.g. the OSS overall-lead). The endpoint
    /// resolves the correct HumanActor by inspecting the refreshed inbox
    /// row's <c>Human</c> field. Idempotent — repeated calls only advance
    /// the cursor. Returns the updated <see cref="InboxItemResponse"/> so
    /// the portal can reconcile the cache in one round-trip.
    /// </summary>
    private static async Task<IResult> MarkReadAsync(
        string threadId,
        IAuthenticatedCallerAccessor callerAccessor,
        IInboxIdentityResolver inboxIdentityResolver,
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

        var caller = await callerAccessor.GetCallerAddressAsync(cancellationToken);
        if (caller is null)
        {
            // mark-read writes a per-thread read cursor on a HumanActor;
            // without an identity there is nothing to advance (#2405).
            return Results.Problem(
                detail: "No authenticated caller identity available.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var humanIds = await inboxIdentityResolver.ResolveHumanIdsAsync(caller, cancellationToken);
        if (humanIds.Count == 0)
        {
            return Results.Problem(
                detail: $"Thread '{threadId}' not found in inbox.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Find the inbox row for this thread to identify which mapped
        // HumanActor received the pending message. The same query also
        // returns the post-mark snapshot we hand back below, so the
        // round-trip is a single ListInboxAsync after the cursor write.
        var preItems = await queryService.ListInboxAsync(humanIds, lastReadAt: null, cancellationToken);
        var targetRow = preItems.FirstOrDefault(i =>
            string.Equals(i.ThreadId, threadId, StringComparison.Ordinal));
        if (targetRow is null)
        {
            return Results.Problem(
                detail: $"Thread '{threadId}' not found in inbox.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (!Address.TryParse(targetRow.Human, out var recipientAddress) || recipientAddress is null)
        {
            // The inbox row's Human field is written by RenderAddress in
            // canonical form; a parse failure means an upstream invariant
            // broke. Surface as 500-via-Problem rather than the previous
            // silent no-op.
            return Results.Problem(
                detail: $"Inbox row for thread '{threadId}' carried a malformed recipient address '{targetRow.Human}'.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var readAt = DateTimeOffset.UtcNow;
        var humanProxy = actorProxyFactory.CreateActorProxy<IHumanActor>(
            new ActorId(recipientAddress.Path), nameof(HumanActor));
        await humanProxy.MarkReadAsync(threadId, readAt, cancellationToken);

        // Recompute the inbox with the post-mark cursor map so the
        // returned item carries the correct UnreadCount (typically 0).
        var lastReadAt = await LoadMergedReadCursorsAsync(humanIds, actorProxyFactory, cancellationToken);
        var items = await queryService.ListInboxAsync(humanIds, lastReadAt, cancellationToken);
        var rawUpdated = items.FirstOrDefault(i => string.Equals(i.ThreadId, threadId, StringComparison.Ordinal));

        if (rawUpdated is null)
        {
            // The row dropped from the inbox between the two passes —
            // typically because mark-read advanced the cursor past the
            // last pending message. Fall back to the pre-mark snapshot
            // so the portal still gets a 200 with the now-resolved
            // identity.
            rawUpdated = targetRow with { UnreadCount = 0 };
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
        var snapshots = item.ParticipantNameSnapshots;
        var from = await ThreadEndpoints.ToRefAsync(item.From, resolver, snapshots, ct);
        var human = await ThreadEndpoints.ToRefAsync(item.Human, resolver, snapshots, ct);
        return new InboxItemResponse(
            item.ThreadId,
            from,
            human,
            item.PendingSince,
            item.Summary,
            item.UnreadCount);
    }
}
