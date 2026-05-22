// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Recipient scopes a <c>sv.messaging.multicast</c> call may target instead
/// of an explicit address list. Resolved against the caller's position in
/// the unit member graph.
/// </summary>
public enum MulticastScope
{
    /// <summary>The members of the calling unit.</summary>
    UnitMembers,

    /// <summary>The caller's siblings — co-members of the caller's parent unit(s).</summary>
    Siblings,
}

/// <summary>
/// Handlers backing the platform messaging tools <c>sv.messaging.send</c>
/// and <c>sv.messaging.multicast</c> (ADR-0048 / ADR-0049). Both are
/// one-way message-delivery tools: the platform durably places the message
/// in the recipient mailbox(es) and returns a delivery acknowledgement —
/// never the recipient's reply. Each call emits a plain
/// <see cref="ActivityEventType.MessageSent"/> activity (best-effort);
/// recording a routing <i>decision</i> is the separate, optional
/// <c>sv.runtime.report_decision</c> tool.
/// </summary>
public class MessagingToolHandlers(
    MessageDeliveryService deliveryService,
    IUnitMemberGraphStore memberGraphStore,
    IServiceScopeFactory scopeFactory,
    IActivityEventBus activityEventBus,
    ILogger<MessagingToolHandlers> logger)
{
    /// <summary>
    /// Delivers <paramref name="message"/> to <paramref name="target"/> and
    /// returns a delivery acknowledgement (ADR-0049). Delivery is synchronous
    /// with bounded retry; the tool never blocks on the recipient's runtime.
    /// Validation failures, hop-budget exhaustion, and terminal delivery
    /// failures throw <see cref="MessageDeliveryException"/>.
    /// </summary>
    public async Task<MessageDeliveryAck> HandleSendAsync(
        Address caller,
        Guid tenantId,
        Address target,
        Message message,
        string? reason,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);

        await deliveryService.EnsureCallerTenantAsync(caller, tenantId, ct);
        MessageDeliveryService.EnsureNotSelfTarget(caller, target);
        await deliveryService.EnsureTargetTenantAsync(target, tenantId, ct);

        // #2576 — one hop per send call, checked before delivery so a cycle
        // terminates at the limit.
        await deliveryService.EnsureHopBudgetAsync(threadId, ct);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Delivering message {MessageId} from {Caller} to {Target} on thread {ThreadId}. Reason: {Reason}",
                message.Id, caller, target, threadId, reason);
        }

        await deliveryService.DeliverWithRetryAsync(caller, target, message, ct);

        await PublishMessageSentAsync(
            caller,
            threadId,
            $"Message sent to {target}.",
            new[] { target },
            reason,
            ct);

        return new MessageDeliveryAck(true, message.Id, target, threadId);
    }

    /// <summary>
    /// Delivers <paramref name="message"/> to every resolved target in
    /// parallel and returns a per-target delivery outcome (ADR-0049). The
    /// recipient set is either <paramref name="explicitTargets"/> or, when
    /// that is <c>null</c>, the addresses resolved from
    /// <paramref name="scope"/>. Synchronous validation failures and
    /// hop-budget exhaustion throw <see cref="MessageDeliveryException"/>
    /// before any delivery attempt; a transient delivery failure surfaces as
    /// that target's outcome, not as a thrown exception.
    /// </summary>
    public async Task<MulticastResult> HandleMulticastAsync(
        Address caller,
        Guid tenantId,
        IReadOnlyList<Address>? explicitTargets,
        MulticastScope? scope,
        Message message,
        string? reason,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(message);

        if (explicitTargets is null && scope is null)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                "sv.messaging.multicast requires exactly one of 'addresses' or 'scope'.");
        }

        if (explicitTargets is not null && scope is not null)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.InvalidRequest,
                "sv.messaging.multicast accepts 'addresses' or 'scope', not both.");
        }

        await deliveryService.EnsureCallerTenantAsync(caller, tenantId, ct);

        var targets = explicitTargets is not null
            ? explicitTargets
            : await ResolveScopeAsync(caller, scope!.Value, ct);

        foreach (var target in targets)
        {
            MessageDeliveryService.EnsureNotSelfTarget(caller, target);
            await deliveryService.EnsureTargetTenantAsync(target, tenantId, ct);
        }

        // #2576 — one hop per multicast call, regardless of fan-out width.
        await deliveryService.EnsureHopBudgetAsync(threadId, ct);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Multicasting message {MessageId} from {Caller} to {TargetCount} target(s) on thread {ThreadId}. Reason: {Reason}",
                message.Id, caller, targets.Count, threadId, reason);
        }

        var tasks = targets
            .Select(target => deliveryService.DeliverOutcomeAsync(caller, target, message, ct))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);

        await PublishMessageSentAsync(
            caller,
            threadId,
            $"Message multicast to {targets.Count} target(s).",
            targets,
            reason,
            ct);

        return new MulticastResult(message.Id, threadId, outcomes);
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
                $"Unknown multicast scope '{scope}'."),
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
                $"Multicast scope 'unit-members' is only valid for a unit:// caller; got '{caller}'.");
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

/// <summary>
/// Result of a <c>sv.messaging.multicast</c> call — the delivered message
/// id, the thread, and a per-target delivery outcome (ADR-0049).
/// </summary>
public sealed record MulticastResult(
    Guid MessageId,
    Guid ThreadId,
    IReadOnlyList<MulticastTargetAck> Deliveries);
