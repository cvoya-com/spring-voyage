// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Delivery acknowledgement for a single <c>delegate_to</c> message-delivery
/// tool call (ADR-0049 §2). It confirms the message was durably placed in the
/// recipient's mailbox — it never carries the recipient's response.
/// </summary>
public sealed record DelegateDeliveryAck(
    bool Delivered,
    Guid MessageId,
    Address Target,
    Guid ThreadId);

/// <summary>
/// Per-target delivery outcome for a <c>fanout_to</c> call (ADR-0049 §2).
/// Reports whether the message reached each recipient's mailbox — not the
/// recipients' work products.
/// </summary>
public sealed record FanoutDeliveryAck(
    Address Target,
    bool Delivered,
    string? Error);

public class OrchestrationToolHandlers(
    IAgentProxyResolver agentProxyResolver,
    ILogger<OrchestrationToolHandlers> logger,
    IActivityEventBus activityEventBus,
    IOrchestrationTenantResolver tenantResolver,
    IOptions<OrchestrationDeliveryOptions>? deliveryOptions = null)
{
    private readonly OrchestrationDeliveryOptions _deliveryOptions =
        deliveryOptions?.Value ?? new OrchestrationDeliveryOptions();

    /// <summary>
    /// Delivers <paramref name="message"/> to <paramref name="target"/> and
    /// returns a delivery acknowledgement (ADR-0049). Delivery is synchronous
    /// with bounded retry; the tool never blocks on the recipient's runtime.
    /// Validation failures and terminal delivery failures throw
    /// <see cref="OrchestrationException"/>.
    /// </summary>
    public async Task<DelegateDeliveryAck> HandleDelegateToAsync(
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

        await EnsureCallerTenantAsync(caller, tenantId, ct);
        EnsureNotSelfTarget(caller, target);
        await EnsureTargetTenantAsync(target, tenantId, ct);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Delegating orchestration message {MessageId} from {Caller} to {Target} on thread {ThreadId}. Reason: {Reason}",
                message.Id,
                caller,
                target,
                threadId,
                reason);
        }

        try
        {
            await DeliverWithRetryAsync(caller, target, message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PublishDecisionAsync(
                caller,
                tenantId,
                threadId,
                message.Id,
                OrchestrationDecisionKind.Delegate,
                [target],
                OrchestrationDecisionStatus.Failed,
                [],
                reason,
                BuildDecisionMetadata(message.Payload),
                CancellationToken.None);

            throw;
        }

        await PublishDecisionAsync(
            caller,
            tenantId,
            threadId,
            message.Id,
            OrchestrationDecisionKind.Delegate,
            [target],
            OrchestrationDecisionStatus.Routed,
            [],
            reason,
            BuildDecisionMetadata(message.Payload),
            ct);

        return new DelegateDeliveryAck(true, message.Id, target, threadId);
    }

    /// <summary>
    /// Delivers <paramref name="message"/> to every target in parallel and
    /// returns a per-target delivery outcome (ADR-0049). Synchronous
    /// validation failures throw <see cref="OrchestrationException"/> before
    /// any delivery attempt; a transient delivery failure surfaces as that
    /// target's outcome, not as a thrown exception.
    /// </summary>
    public async Task<IReadOnlyList<FanoutDeliveryAck>> HandleFanoutToAsync(
        Address caller,
        Guid tenantId,
        IReadOnlyList<Address> targets,
        Message message,
        string? reason,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(message);

        await EnsureCallerTenantAsync(caller, tenantId, ct);
        foreach (var target in targets)
        {
            EnsureNotSelfTarget(caller, target);
            await EnsureTargetTenantAsync(target, tenantId, ct);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Fanning out orchestration message {MessageId} from {Caller} to {TargetCount} targets on thread {ThreadId}. Reason: {Reason}",
                message.Id,
                caller,
                targets.Count,
                threadId,
                reason);
        }

        var tasks = targets
            .Select(target => DeliverOutcomeAsync(caller, target, message, ct))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);
        var status = outcomes.Any(outcome => !outcome.Delivered)
            ? OrchestrationDecisionStatus.Failed
            : OrchestrationDecisionStatus.Routed;

        await PublishDecisionAsync(
            caller,
            tenantId,
            threadId,
            message.Id,
            OrchestrationDecisionKind.Fanout,
            targets.ToArray(),
            status,
            [],
            reason,
            metadata: null,
            ct);

        return outcomes;
    }

    private async Task PublishDecisionAsync(
        Address caller,
        Guid tenantId,
        Guid threadId,
        Guid inputMessageId,
        OrchestrationDecisionKind kind,
        Address[] targets,
        OrchestrationDecisionStatus status,
        Guid[] resultMessageIds,
        string? reason,
        JsonElement? metadata,
        CancellationToken ct)
    {
        var decision = new OrchestrationDecision(
            Guid.NewGuid(),
            tenantId,
            caller,
            threadId,
            inputMessageId,
            kind,
            targets,
            status,
            resultMessageIds,
            reason,
            metadata,
            DateTimeOffset.UtcNow);

        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            decision.CreatedAt,
            caller,
            ActivityEventType.DecisionMade,
            status == OrchestrationDecisionStatus.Failed ? ActivitySeverity.Warning : ActivitySeverity.Info,
            BuildDecisionSummary(kind, status, targets.Length),
            JsonSerializer.SerializeToElement(decision),
            threadId.ToString("D"));

        try
        {
            await activityEventBus.PublishAsync(activityEvent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to emit orchestration decision {DecisionId} for caller {CallerAddress}.",
                decision.DecisionId,
                caller);
        }
    }

    private static string BuildDecisionSummary(
        OrchestrationDecisionKind kind,
        OrchestrationDecisionStatus status,
        int targetCount)
    {
        return kind switch
        {
            OrchestrationDecisionKind.Delegate =>
                $"Orchestration delegate decision {status} for 1 target.",
            OrchestrationDecisionKind.Fanout =>
                $"Orchestration fanout decision {status} for {targetCount} targets.",
            _ =>
                $"Orchestration decision {status}.",
        };
    }

    private static JsonElement? BuildDecisionMetadata(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.String
            || !string.Equals(source.GetString(), "github", StringComparison.OrdinalIgnoreCase)
            || !payload.TryGetProperty("issue", out var issue)
            || issue.ValueKind != JsonValueKind.Object
            || !TryReadPositiveInt(issue, "number", out var issueNumber))
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new
        {
            issue = new { number = issueNumber },
        });
    }

    private static bool TryReadPositiveInt(JsonElement parent, string property, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private async Task EnsureCallerTenantAsync(
        Address caller,
        Guid expectedTenantId,
        CancellationToken ct)
    {
        // ADR-0039 §3 gate 6 — cross-tenant containment, caller side.
        // The validated callback token claims a tenant; the resolved
        // tenant of the caller must match. The signing-key partition
        // ITenantSigningKeyProvider applies on token validation already
        // makes a forged cross-tenant token structurally implausible,
        // but the platform-side handler enforces the gate explicitly so
        // any future authentication shape (mTLS, OIDC) inherits the
        // same containment without re-deriving it from the signing-key
        // story.
        var resolved = await tenantResolver.GetTenantForAddressAsync(caller, ct);
        if (resolved != expectedTenantId)
        {
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationCrossTenant,
                $"Caller '{caller}' belongs to tenant '{GuidFormatter.Format(resolved)}' " +
                $"but the callback token claims tenant '{GuidFormatter.Format(expectedTenantId)}'.");
        }
    }

    private async Task EnsureTargetTenantAsync(
        Address target,
        Guid expectedTenantId,
        CancellationToken ct)
    {
        // ADR-0039 §3 gate 6 — cross-tenant containment, target side.
        // Evaluating the gate on the target prevents a directory-level
        // bug that crosses a tenant boundary from leaking through the
        // orchestration surface.
        var resolved = await tenantResolver.GetTenantForAddressAsync(target, ct);
        if (resolved != expectedTenantId)
        {
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationCrossTenant,
                $"Target '{target}' belongs to tenant '{GuidFormatter.Format(resolved)}' " +
                $"but the callback token claims tenant '{GuidFormatter.Format(expectedTenantId)}'.");
        }
    }

    private static void EnsureNotSelfTarget(Address caller, Address target)
    {
        if (AddressEquals(caller, target))
        {
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationSelfDelegation,
                $"Caller '{caller}' cannot orchestrate against itself.");
        }
    }

    /// <summary>
    /// Delivers a message to one target with bounded retry (ADR-0049 §4). A
    /// successful <c>ReceiveAsync</c> is a durable mailbox enqueue and the
    /// fast-enqueue invariant (§5) means it returns in milliseconds — the
    /// loop blocks only on the enqueue, never on the recipient's runtime.
    /// Only transient infrastructure failures are retried; an
    /// <see cref="OrchestrationException"/> (validation) is never retried and
    /// propagates immediately. A retry budget exhausted by transient failures
    /// surfaces as a terminal <see cref="OrchestrationException"/>.
    /// </summary>
    private async Task DeliverWithRetryAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct)
    {
        var proxy = agentProxyResolver.Resolve(target.Scheme, GuidFormatter.Format(target.Id))
            ?? throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationDeliveryFailed,
                $"Could not resolve orchestration target '{target}'.");

        var outbound = message with
        {
            From = caller,
            To = target,
        };

        var deadline = DateTimeOffset.UtcNow + _deliveryOptions.Budget;
        var backoff = _deliveryOptions.InitialBackoff;
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= _deliveryOptions.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await proxy.ReceiveAsync(outbound, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OrchestrationException)
            {
                // Validation rejection from the recipient — never transient.
                throw;
            }
            catch (Exception ex)
            {
                // ADR-0049 §4 — the only thing that can fail a mailbox
                // enqueue is transient infrastructure. Retry within budget.
                lastTransient = ex;
                logger.LogWarning(
                    ex,
                    "Transient delivery failure for orchestration message {MessageId} to {Target} (attempt {Attempt}/{MaxAttempts}).",
                    message.Id,
                    target,
                    attempt,
                    _deliveryOptions.MaxAttempts);
            }

            if (attempt >= _deliveryOptions.MaxAttempts)
            {
                break;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = backoff < remaining ? backoff : remaining;
            await Task.Delay(delay, ct);
            backoff += backoff;
        }

        throw new OrchestrationException(
            OrchestrationException.RejectCodes.OrchestrationDeliveryFailed,
            $"Delivery of orchestration message '{message.Id}' to '{target}' failed after " +
            $"{_deliveryOptions.MaxAttempts} attempt(s) within the delivery budget.",
            lastTransient ?? new InvalidOperationException("Delivery failed."));
    }

    private async Task<FanoutDeliveryAck> DeliverOutcomeAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct)
    {
        try
        {
            await DeliverWithRetryAsync(caller, target, message, ct);
            return new FanoutDeliveryAck(target, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FanoutDeliveryAck(target, false, ex.Message);
        }
    }

    private static bool AddressEquals(Address left, Address right) =>
        left.Id == right.Id &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase);
}
