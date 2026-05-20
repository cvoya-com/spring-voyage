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

public class OrchestrationToolHandlers(
    IAgentProxyResolver agentProxyResolver,
    OrchestrationDepthCounter depthCounter,
    ILogger<OrchestrationToolHandlers> logger,
    IActivityEventBus activityEventBus,
    IOrchestrationTenantResolver tenantResolver)
{
    public async Task<Message?> HandleDelegateToAsync(
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

        using var depthScope = depthCounter.Increment(threadId);

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

        Message? response;

        try
        {
            response = await SendToTargetAsync(caller, target, message, ct);
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
            response is null ? [] : [response.Id],
            reason,
            BuildDecisionMetadata(message.Payload),
            ct);

        return response;
    }

    public async Task<(Address Target, Message? Response, Exception? Error)[]> HandleFanoutToAsync(
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

        using var depthScope = depthCounter.Increment(threadId);

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

        var tasks = targets.Select(target => SendToTargetResultAsync(caller, target, message, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        var status = results.Any(result => result.Error is not null)
            ? OrchestrationDecisionStatus.Failed
            : OrchestrationDecisionStatus.Routed;
        var resultMessageIds = results
            .Where(result => result.Response is not null)
            .Select(result => result.Response!.Id)
            .ToArray();

        await PublishDecisionAsync(
            caller,
            tenantId,
            threadId,
            message.Id,
            OrchestrationDecisionKind.Fanout,
            targets.ToArray(),
            status,
            resultMessageIds,
            reason,
            metadata: null,
            ct);

        return results;
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

    private async Task<Message?> SendToTargetAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct)
    {
        var proxy = agentProxyResolver.Resolve(target.Scheme, GuidFormatter.Format(target.Id))
            ?? throw new InvalidOperationException($"Could not resolve orchestration target '{target}'.");

        var outbound = message with
        {
            From = caller,
            To = target,
        };

        return await proxy.ReceiveAsync(outbound, ct);
    }

    private async Task<(Address Target, Message? Response, Exception? Error)> SendToTargetResultAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct)
    {
        try
        {
            var response = await SendToTargetAsync(caller, target, message, ct);
            return (target, response, null);
        }
        catch (Exception ex)
        {
            return (target, null, ex);
        }
    }

    private static bool AddressEquals(Address left, Address right) =>
        left.Id == right.Id &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase);
}
