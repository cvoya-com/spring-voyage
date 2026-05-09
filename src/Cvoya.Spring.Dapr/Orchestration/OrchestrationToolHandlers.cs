// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

public class OrchestrationToolHandlers(
    IActorProxyFactory actorProxyFactory,
    IAgentProxyResolver agentProxyResolver,
    OrchestrationDepthCounter depthCounter,
    ILogger<OrchestrationToolHandlers> logger,
    IActivityEventBus activityEventBus)
{
    public async Task<Address[]> HandleListChildrenAsync(
        Address caller,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        EnsureUnitCaller(caller);

        // no OrchestrationDecision event per ADR-0039 §4.
        return await ReadMembersAsync(caller, ct);
    }

    public async Task<IReadOnlyDictionary<string, object?>> HandleInspectChildAsync(
        Address caller,
        Address target,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);

        EnsureUnitCaller(caller);
        await EnsureDirectChildrenAsync(caller, [target], ct);

        // no OrchestrationDecision event per ADR-0039 §4.
        return new Dictionary<string, object?>
        {
            ["scheme"] = target.Scheme,
            ["id"] = target.Id,
        };
    }

    public async Task<Message?> HandleDelegateToChildAsync(
        Address caller,
        Address target,
        Message message,
        string? reason,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);

        EnsureUnitCaller(caller);
        await EnsureDirectChildrenAsync(caller, [target], ct);

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

    public async Task<(Address Target, Message? Response, Exception? Error)[]> HandleFanoutToChildrenAsync(
        Address caller,
        IReadOnlyList<Address> targets,
        Message message,
        string? reason,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(message);

        EnsureUnitCaller(caller);
        await EnsureDirectChildrenAsync(caller, targets, ct);

        using var depthScope = depthCounter.Increment(threadId);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            logger.LogInformation(
                "Fanning out orchestration message {MessageId} from {Caller} to {TargetCount} children on thread {ThreadId}. Reason: {Reason}",
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

    public async Task<string> HandleQueryChildStatusAsync(
        Address caller,
        Address target,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);

        EnsureUnitCaller(caller);
        await EnsureDirectChildrenAsync(caller, [target], ct);

        // no OrchestrationDecision event per ADR-0039 §4.
        // ADR-0039 / #1826 v0.1 default: child status has no concrete source yet.
        return "unknown";
    }

    private async Task PublishDecisionAsync(
        Address caller,
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
        // TODO #1994: replace Guid.Empty with the callback token tenant id once
        // the orchestration handler receives the invocation scope.
        var decision = new OrchestrationDecision(
            Guid.NewGuid(),
            Guid.Empty,
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
                "Failed to emit orchestration decision {DecisionId} for unit {UnitAddress}.",
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

    private static void EnsureUnitCaller(Address caller)
    {
        if (!string.Equals(caller.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationCallerIsNotUnit,
                $"Orchestration tools can only be invoked by unit callers. Caller was '{caller}'.");
        }
    }

    private async Task EnsureDirectChildrenAsync(
        Address caller,
        IReadOnlyList<Address> targets,
        CancellationToken ct)
    {
        var members = await ReadMembersAsync(caller, ct);

        foreach (var target in targets)
        {
            if (AddressEquals(caller, target))
            {
                throw new OrchestrationException(
                    OrchestrationException.RejectCodes.OrchestrationSelfDelegation,
                    $"Unit '{caller}' cannot delegate orchestration work to itself.");
            }

            if (!members.Any(member => AddressEquals(member, target)))
            {
                throw new OrchestrationException(
                    OrchestrationException.RejectCodes.OrchestrationTargetNotChild,
                    $"Target '{target}' is not a direct child of '{caller}'.");
            }
        }
    }

    private async Task<Address[]> ReadMembersAsync(Address caller, CancellationToken ct)
    {
        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(GuidFormatter.Format(caller.Id)),
            nameof(UnitActor));

        return await proxy.GetMembersAsync(ct);
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