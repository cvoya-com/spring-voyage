// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Recipient scopes a messaging-tool call may target instead of an explicit
/// address list. Resolved against the caller's position in the unit member
/// graph. <c>sv.messaging.send</c> treats the scope as the participants of
/// a single shared thread; <c>sv.messaging.multicast</c> treats it as a
/// fan-out list across N independent 1-1 threads (#2747).
/// </summary>
public enum MulticastScope
{
    /// <summary>The members of the calling unit.</summary>
    UnitMembers,

    /// <summary>The caller's siblings — co-members of the caller's parent unit(s).</summary>
    Siblings,
}

/// <summary>
/// Outcome of one <c>sv.messaging.send</c> call (#2747). The call delivers
/// one message to every resolved recipient on the <i>single shared thread</i>
/// identified by <c>{caller} ∪ recipients</c>. Each <see cref="Deliveries"/>
/// row confirms enqueue to that recipient's mailbox; the platform never
/// carries the recipient's response (ADR-0049).
/// </summary>
public sealed record SendResult(
    Guid MessageId,
    Guid ThreadId,
    IReadOnlyList<RecipientDeliveryAck> Deliveries);

/// <summary>
/// Outcome of one <c>sv.messaging.multicast</c> call (#2747). The call
/// delivers the same message to every resolved recipient on its own
/// independent 1-1 thread, so each <see cref="Deliveries"/> row carries its
/// own <see cref="RecipientDeliveryAck.ThreadId"/>.
/// </summary>
public sealed record MulticastResult(
    Guid MessageId,
    IReadOnlyList<RecipientDeliveryAck> Deliveries);

/// <summary>
/// Per-recipient delivery acknowledgement for a messaging-tool call
/// (ADR-0049). <see cref="ThreadId"/> is the thread the recipient saw the
/// message on — shared across all deliveries for <c>sv.messaging.send</c>,
/// per-pair for <c>sv.messaging.multicast</c>.
/// </summary>
public sealed record RecipientDeliveryAck(
    Address Target,
    bool Delivered,
    Guid ThreadId,
    string? Error);

/// <summary>
/// Handlers backing the platform messaging tools <c>sv.messaging.send</c>
/// and <c>sv.messaging.multicast</c> (ADR-0048 / ADR-0049, reshaped by
/// #2747). Both tools accept either an explicit recipient list OR a scope
/// (<c>"unit-members"</c> / <c>"siblings"</c>); they differ in thread
/// identity, not input shape:
/// <list type="bullet">
///   <item><description><c>sv.messaging.send</c> — one shared thread for the
///   whole participant set <c>{caller} ∪ recipients</c>. Every recipient
///   sees the others in the inbound envelope's <c>to</c> field.</description></item>
///   <item><description><c>sv.messaging.multicast</c> — N independent 1-1
///   threads <c>{caller, recipient_i}</c>. Each recipient sees only itself
///   in the inbound envelope.</description></item>
/// </list>
/// Both calls emit a plain <see cref="ActivityEventType.MessageSent"/>
/// activity (best-effort); routing decisions are emitted separately via
/// <c>sv.runtime.report_decision</c>.
/// </summary>
public class MessagingToolHandlers(
    MessageDeliveryService deliveryService,
    IUnitMemberGraphStore memberGraphStore,
    IServiceScopeFactory scopeFactory,
    IActivityEventBus activityEventBus,
    ILogger<MessagingToolHandlers> logger)
{
    /// <summary>
    /// Delivers <paramref name="message"/> to every resolved recipient on
    /// the single shared thread <c>{caller} ∪ recipients</c> (#2747). Each
    /// recipient receives the message on the same <see cref="SendResult.ThreadId"/>;
    /// a subsequent <c>sv.memory.history_with([…recipients])</c> by any
    /// participant returns this thread's full timeline. Validation
    /// failures, hop-budget exhaustion, and unroutable recipients (e.g.
    /// <c>connector://</c>) throw <see cref="MessageDeliveryException"/>
    /// before any delivery attempt.
    /// </summary>
    public async Task<SendResult> HandleSendAsync(
        Address caller,
        Guid tenantId,
        IReadOnlyList<Address>? explicitRecipients,
        MulticastScope? scope,
        Message message,
        string? reason,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(message);

        await deliveryService.EnsureCallerTenantAsync(caller, tenantId, ct);

        var recipients = await ResolveRecipientsAsync(
            caller, explicitRecipients, scope, "sv.messaging.send", ct);

        foreach (var recipient in recipients)
        {
            MessageDeliveryService.EnsureNotSelfTarget(caller, recipient);
            MessageDeliveryService.EnsureCanReceive(recipient);
            await deliveryService.EnsureTargetTenantAsync(recipient, tenantId, ct);
        }

        // The shared-thread participant set: caller plus every recipient.
        // IThreadRegistry canonicalises (order-insensitive, dedupe), so the
        // same set always resolves to the same thread id — that is the
        // identity ADR-0030 calls out.
        var participantSet = new List<Address>(recipients.Count + 1) { caller };
        participantSet.AddRange(recipients);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Sending message {MessageId} from {Caller} to {RecipientCount} recipient(s) on shared thread (upstream thread {ThreadId}). Reason: {Reason}",
                message.Id, caller, recipients.Count, threadId, reason);
        }

        var tasks = recipients
            .Select(target => deliveryService.DeliverOutcomeAsync(
                caller, target, message, ct, threadParticipants: participantSet))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        // All deliveries land on the same shared thread; surface that id on
        // the top-level result and on each per-recipient row.
        var sharedThreadId = await ResolveThreadIdAsync(participantSet, ct);
        var deliveries = outcomes
            .Select(o => new RecipientDeliveryAck(o.Target, o.Delivered, sharedThreadId, o.Error))
            .ToList();

        await PublishMessageSentAsync(
            caller,
            threadId,
            recipients.Count == 1
                ? $"Message sent to {recipients[0]}."
                : $"Message sent to {recipients.Count} participant(s) on a shared thread.",
            recipients,
            reason,
            ct);

        return new SendResult(message.Id, sharedThreadId, deliveries);
    }

    /// <summary>
    /// Delivers <paramref name="message"/> to every resolved recipient on
    /// its own independent 1-1 thread <c>{caller, recipient_i}</c> (#2747).
    /// Each recipient sees only itself in the inbound envelope's <c>to</c>
    /// field, and a subsequent <c>sv.memory.history_with([recipient_i])</c>
    /// returns only that pair's history. Validation failures, hop-budget
    /// exhaustion, and unroutable recipients (e.g. <c>connector://</c>)
    /// throw <see cref="MessageDeliveryException"/> before any delivery
    /// attempt; a transient delivery failure surfaces as that recipient's
    /// outcome, not a thrown exception.
    /// </summary>
    public async Task<MulticastResult> HandleMulticastAsync(
        Address caller,
        Guid tenantId,
        IReadOnlyList<Address>? explicitRecipients,
        MulticastScope? scope,
        Message message,
        string? reason,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(message);

        await deliveryService.EnsureCallerTenantAsync(caller, tenantId, ct);

        var recipients = await ResolveRecipientsAsync(
            caller, explicitRecipients, scope, "sv.messaging.multicast", ct);

        foreach (var recipient in recipients)
        {
            MessageDeliveryService.EnsureNotSelfTarget(caller, recipient);
            MessageDeliveryService.EnsureCanReceive(recipient);
            await deliveryService.EnsureTargetTenantAsync(recipient, tenantId, ct);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Multicasting message {MessageId} from {Caller} to {RecipientCount} independent 1-1 thread(s) (upstream thread {ThreadId}). Reason: {Reason}",
                message.Id, caller, recipients.Count, threadId, reason);
        }

        var tasks = recipients
            .Select(target => deliveryService.DeliverOutcomeAsync(caller, target, message, ct))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        // Each recipient lands on its own (caller, recipient) thread; rehydrate
        // the per-pair id so callers see the thread their message actually
        // landed on (mirrors MessageDeliveryService.ResolveHopThreadAsync).
        var perRecipient = new RecipientDeliveryAck[outcomes.Length];
        for (var i = 0; i < outcomes.Length; i++)
        {
            var pairThreadId = await ResolveThreadIdAsync(
                new[] { caller, outcomes[i].Target }, ct);
            perRecipient[i] = new RecipientDeliveryAck(
                outcomes[i].Target, outcomes[i].Delivered, pairThreadId, outcomes[i].Error);
        }

        await PublishMessageSentAsync(
            caller,
            threadId,
            $"Message multicast to {recipients.Count} independent 1-1 thread(s).",
            recipients,
            reason,
            ct);

        return new MulticastResult(message.Id, perRecipient);
    }

    /// <summary>
    /// Continues the conversation that <paramref name="targetMessageId"/>
    /// belongs to (ADR-0064 — <c>sv.messaging.respond_to</c>). Resolves the
    /// message to its thread, takes that thread's current routable
    /// participant set, and delivers <paramref name="message"/> one-way to
    /// every routable participant except the caller — on the <i>same</i>
    /// thread, so the conversation does not fork. The platform picks the
    /// recipients (the conversation's roster); the caller never reconstructs
    /// the recipient list. Because <paramref name="targetMessageId"/> is
    /// durable, this also serves a later continuation. Validation failures,
    /// hop-budget exhaustion, and an unresolvable / participant-less
    /// conversation throw <see cref="MessageDeliveryException"/> before any
    /// delivery attempt.
    /// </summary>
    public async Task<SendResult> HandleRespondToAsync(
        Address caller,
        Guid tenantId,
        Guid targetMessageId,
        Message message,
        string? reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(message);

        await deliveryService.EnsureCallerTenantAsync(caller, tenantId, ct);

        var (threadId, roster) = await ResolveConversationAsync(targetMessageId, ct);

        // Recipients = the conversation's routable participants, minus the
        // caller. A non-routable origin (a connector that stamped an inbound
        // event) is never a delivery target; the caller is excluded because
        // respondTo never echoes to self.
        var callerKey = caller.ToString();
        var recipients = roster
            .Where(a => a.IsRoutable
                && !string.Equals(a.ToString(), callerKey, StringComparison.Ordinal))
            .ToList();

        if (recipients.Count == 0)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                $"sv.messaging.respond_to resolved zero recipients for message " +
                $"'{GuidFormatter.Format(targetMessageId)}': the conversation has no routable " +
                "participant other than you.");
        }

        foreach (var recipient in recipients)
        {
            MessageDeliveryService.EnsureNotSelfTarget(caller, recipient);
            MessageDeliveryService.EnsureCanReceive(recipient);
            await deliveryService.EnsureTargetTenantAsync(recipient, tenantId, ct);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Responding to conversation of message {TargetMessageId} from {Caller} to {RecipientCount} participant(s) on thread {ThreadId}. Reason: {Reason}",
                targetMessageId, caller, recipients.Count, threadId, reason);
        }

        // Deliver on the conversation's existing thread: pass its full stored
        // roster as the thread participant set so GetOrCreateAsync resolves
        // the same id and the continuation lands on the same thread (no
        // fork) — even when the roster carries a member we don't deliver to.
        var tasks = recipients
            .Select(target => deliveryService.DeliverOutcomeAsync(
                caller, target, message, ct, threadParticipants: roster))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        var deliveries = outcomes
            .Select(o => new RecipientDeliveryAck(o.Target, o.Delivered, threadId, o.Error))
            .ToList();

        await PublishMessageSentAsync(
            caller,
            threadId,
            $"Responded to the conversation of message {GuidFormatter.Format(targetMessageId)} ({recipients.Count} participant(s)).",
            recipients,
            reason,
            ct);

        return new SendResult(message.Id, threadId, deliveries);
    }

    /// <summary>
    /// Resolves the conversation a message belongs to: its thread id and the
    /// thread's canonical participant roster. Backs
    /// <see cref="HandleRespondToAsync"/>. <see cref="IMessageQueryService"/>
    /// reads the EF-authoritative <c>messages</c> table (ADR-0030/0040) for
    /// the message's thread id; <see cref="IThreadRegistry"/> resolves that
    /// id to its participant set. Both are scoped (EF-backed) while this
    /// service is a singleton, so a scope is opened per call — the same
    /// pattern <see cref="ResolveThreadIdAsync"/> uses. Throws
    /// <see cref="MessageDeliveryException"/> when the message is unknown,
    /// unthreaded, or its thread has no participants.
    /// </summary>
    private async Task<(Guid ThreadId, IReadOnlyList<Address> Roster)> ResolveConversationAsync(
        Guid targetMessageId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var messageQuery = scope.ServiceProvider.GetRequiredService<IMessageQueryService>();
        var threadRegistry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();

        var detail = await messageQuery.GetAsync(targetMessageId, ct);
        if (detail is null || string.IsNullOrWhiteSpace(detail.ThreadId))
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                $"sv.messaging.respond_to could not resolve a conversation for message " +
                $"'{GuidFormatter.Format(targetMessageId)}': no such message, or it was not threaded.");
        }

        var entry = await threadRegistry.ResolveAsync(detail.ThreadId, ct);
        if (entry is null || entry.Participants.Count == 0)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                $"sv.messaging.respond_to could not resolve the participant set for the conversation of " +
                $"message '{GuidFormatter.Format(targetMessageId)}'.");
        }

        if (!GuidFormatter.TryParse(entry.ThreadId, out var threadGuid))
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                $"Thread id '{entry.ThreadId}' for the conversation of message " +
                $"'{GuidFormatter.Format(targetMessageId)}' is not a valid Guid.");
        }

        return (threadGuid, entry.Participants);
    }

    /// <summary>
    /// Resolves the recipient set for a messaging-tool call: either the
    /// explicit list or the addresses resolved from <paramref name="scope"/>.
    /// Throws <see cref="MessageDeliveryException"/> when neither or both
    /// are supplied. Duplicates are collapsed (deterministic by canonical
    /// address) — including the caller's own address, since the caller is
    /// auto-included in every participant set and listing themselves is a
    /// no-op (a recipient list is a set, not an order-significant list).
    /// An all-caller list (e.g. <c>recipients=[caller]</c>) becomes an
    /// empty set and surfaces as <c>InvalidRequest</c>.
    /// </summary>
    private async Task<IReadOnlyList<Address>> ResolveRecipientsAsync(
        Address caller,
        IReadOnlyList<Address>? explicitRecipients,
        MulticastScope? scope,
        string toolName,
        CancellationToken ct)
    {
        var hasRecipients = explicitRecipients is { Count: > 0 };
        var hasScope = scope is not null;

        if (hasRecipients == hasScope)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                $"{toolName} requires exactly one of a non-empty 'recipients' array or a 'scope' string.");
        }

        var resolved = hasRecipients
            ? explicitRecipients!
            : await ResolveScopeAsync(caller, scope!.Value, ct);

        // Dedupe (including the caller, who is auto-included in the
        // participant set and listing themselves is a no-op) while
        // preserving order so logs and per-recipient acks mirror the
        // caller's intent. A scope resolver never returns the caller, so
        // this only filters explicit-list duplicates / explicit self-listing.
        var callerKey = caller.ToString();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<Address>(resolved.Count);
        foreach (var address in resolved)
        {
            var key = address.ToString();
            if (string.Equals(key, callerKey, StringComparison.Ordinal))
            {
                continue;
            }
            if (seen.Add(key))
            {
                deduped.Add(address);
            }
        }

        if (deduped.Count == 0)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                hasScope
                    ? $"{toolName} resolved zero recipients (scope='{scope}' yielded an empty set)."
                    : $"{toolName} resolved zero recipients (the only address supplied was the caller — recipients is a set and the caller is auto-included).");
        }

        return deduped;
    }

    private async Task<Guid> ResolveThreadIdAsync(
        IReadOnlyCollection<Address> participants, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();
        var id = await registry.GetOrCreateAsync(participants, ct);
        return GuidFormatter.TryParse(id, out var guid) ? guid : Guid.Empty;
    }

    /// <summary>
    /// Resolves a <see cref="MulticastScope"/> to a concrete address set.
    /// <see cref="MulticastScope.UnitMembers"/> reads the members of the
    /// caller (valid only when the caller is a <c>unit://</c> address);
    /// <see cref="MulticastScope.Siblings"/> walks to the caller's parent
    /// unit(s) and returns their members, excluding the caller. The
    /// resolution mirrors <c>SvDirectorySkillRegistry.ListMembersAsync</c> /
    /// <c>GetSiblingsAsync</c>.
    /// </summary>
    private async Task<IReadOnlyList<Address>> ResolveScopeAsync(
        Address caller,
        MulticastScope scope,
        CancellationToken ct)
    {
        return scope switch
        {
            MulticastScope.UnitMembers => await ResolveUnitMembersAsync(caller, ct),
            MulticastScope.Siblings => await ResolveSiblingsAsync(caller, ct),
            _ => throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                $"Unknown scope '{scope}'."),
        };
    }

    private async Task<IReadOnlyList<Address>> ResolveUnitMembersAsync(
        Address caller,
        CancellationToken ct)
    {
        if (!string.Equals(caller.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                $"Scope 'unit-members' is only valid for a unit:// caller; got '{caller}'.");
        }

        return await memberGraphStore.GetMembersAsync(caller.Id, ct);
    }

    private async Task<IReadOnlyList<Address>> ResolveSiblingsAsync(
        Address caller,
        CancellationToken ct)
    {
        var parentIds = await ResolveParentUnitIdsAsync(caller, ct);

        var siblingsByPath = new Dictionary<string, Address>(StringComparer.Ordinal);
        var callerPath = GuidFormatter.Format(caller.Id);

        foreach (var parentId in parentIds)
        {
            var members = await memberGraphStore.GetMembersAsync(parentId, ct);
            foreach (var member in members)
            {
                if (string.Equals(member.Path, callerPath, StringComparison.Ordinal))
                {
                    continue;
                }

                siblingsByPath[member.Path] = member;
            }
        }

        return siblingsByPath.Values.ToList();
    }

    /// <summary>
    /// Resolves the parent unit ids of <paramref name="caller"/> — agent
    /// callers via <see cref="IUnitMembershipRepository"/>, unit callers via
    /// <see cref="IUnitSubunitMembershipRepository"/>. Mirrors
    /// <c>SvDirectorySkillRegistry.ResolveParentUuidsAsync</c>.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveParentUnitIdsAsync(
        Address caller,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();

        if (string.Equals(caller.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase))
        {
            var memberships = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
            var rows = await memberships.ListByAgentAsync(caller.Id, ct);
            return rows.Select(r => r.UnitId).Distinct().ToList();
        }

        if (string.Equals(caller.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            var subunits = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
            var rows = await subunits.ListByChildAsync(caller.Id, ct);
            return rows.Select(r => r.ParentId).Distinct().ToList();
        }

        return Array.Empty<Guid>();
    }

    /// <summary>
    /// Publishes a best-effort <see cref="ActivityEventType.MessageSent"/>
    /// activity for a delivery. Failure to publish never propagates — the
    /// activity is a diagnostic signal, not load-bearing for delivery.
    /// </summary>
    private async Task PublishMessageSentAsync(
        Address caller,
        Guid threadId,
        string summary,
        IReadOnlyList<Address> targets,
        string? reason,
        CancellationToken ct)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            targets = targets.Select(t => t.ToString()).ToArray(),
            reason,
        });

        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            caller,
            ActivityEventType.MessageSent,
            ActivitySeverity.Info,
            summary,
            details,
            threadId == Guid.Empty ? null : threadId.ToString("D"));

        try
        {
            await activityEventBus.PublishAsync(activityEvent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to emit MessageSent activity for caller {CallerAddress}.",
                caller);
        }
    }
}
