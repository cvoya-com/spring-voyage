// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Lifecycle;
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
    IActivityEventBus activityEventBus,
    IOrchestrationTenantResolver tenantResolver)
{
    public async Task<OrchestrationChildDescriptor[]> HandleListChildrenAsync(
        Address caller,
        Guid tenantId,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        EnsureUnitCaller(caller);
        await EnsureCallerTenantAsync(caller, tenantId, ct);

        // no OrchestrationDecision event per ADR-0039 §4.
        return await ReadChildDescriptorsAsync(caller, ct);
    }

    public async Task<IReadOnlyDictionary<string, object?>> HandleInspectChildAsync(
        Address caller,
        Guid tenantId,
        Address target,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);

        EnsureUnitCaller(caller);
        await EnsureCallerTenantAsync(caller, tenantId, ct);
        await EnsureDirectChildrenAsync(caller, [target], ct);
        await EnsureTargetTenantAsync(target, tenantId, ct);

        // no OrchestrationDecision event per ADR-0039 §4.
        // The schema requires { address, displayName, kind } and optionally
        // { description, expertise, status }. v0.1 emits the required three
        // fields plus a best-effort status probe; the optional description /
        // expertise slots stay empty because the dispatcher process does
        // not have the directory metadata wired and a Dapr round-trip per
        // probe would dwarf the cost of the real inspect call (callers
        // that need richer detail can issue a separate read).
        var descriptor = await ReadSingleChildDescriptorAsync(caller, target, ct);
        var status = await TryProbeChildStatusAsync(target, ct);

        return new Dictionary<string, object?>
        {
            ["address"] = target.ToString(),
            ["displayName"] = descriptor?.DisplayName ?? string.Empty,
            ["kind"] = descriptor?.Kind ?? ResolveKind(target),
            ["status"] = status.Status,
        };
    }

    public async Task<Message?> HandleDelegateToChildAsync(
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

        EnsureUnitCaller(caller);
        await EnsureCallerTenantAsync(caller, tenantId, ct);
        await EnsureDirectChildrenAsync(caller, [target], ct);
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

    public async Task<(Address Target, Message? Response, Exception? Error)[]> HandleFanoutToChildrenAsync(
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

        EnsureUnitCaller(caller);
        await EnsureCallerTenantAsync(caller, tenantId, ct);
        await EnsureDirectChildrenAsync(caller, targets, ct);
        foreach (var target in targets)
        {
            await EnsureTargetTenantAsync(target, tenantId, ct);
        }

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

    public async Task<ChildStatusResult> HandleQueryChildStatusAsync(
        Address caller,
        Guid tenantId,
        Address target,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);

        EnsureUnitCaller(caller);
        await EnsureCallerTenantAsync(caller, tenantId, ct);
        await EnsureDirectChildrenAsync(caller, [target], ct);
        await EnsureTargetTenantAsync(target, tenantId, ct);

        // no OrchestrationDecision event per ADR-0039 §4.
        return await TryProbeChildStatusAsync(target, ct);
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
        // Direct-child membership (gate 3) already implies same-tenant
        // containment under normal operation, but evaluating gate 6 on
        // the target independently prevents a directory-level bug that
        // crosses a tenant boundary from leaking through the
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

    private async Task<OrchestrationChildDescriptor[]> ReadChildDescriptorsAsync(Address caller, CancellationToken ct)
    {
        var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(GuidFormatter.Format(caller.Id)),
            nameof(UnitActor));

        return await proxy.GetChildDescriptorsAsync(ct);
    }

    private async Task<OrchestrationChildDescriptor?> ReadSingleChildDescriptorAsync(
        Address caller,
        Address target,
        CancellationToken ct)
    {
        var descriptors = await ReadChildDescriptorsAsync(caller, ct);
        return descriptors.FirstOrDefault(d => AddressEquals(d.Address, target));
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

    /// <summary>
    /// Probes the target child's lifecycle status by sending a
    /// <see cref="MessageType.StatusQuery"/> control message through the
    /// existing actor mailbox. The response payload's <c>Status</c>
    /// (<see cref="AgentStatus"/> for agents, <see cref="Core.Units.LifecycleStatus"/>
    /// for units) is mapped onto the closed schema enum
    /// (<c>ready | busy | stopped | error | unknown</c>).
    /// </summary>
    private async Task<ChildStatusResult> TryProbeChildStatusAsync(Address target, CancellationToken ct)
    {
        try
        {
            var proxy = agentProxyResolver.Resolve(target.Scheme, GuidFormatter.Format(target.Id));
            if (proxy is null)
            {
                return new ChildStatusResult("unknown");
            }

            var probe = new Message(
                Guid.NewGuid(),
                target,
                target,
                MessageType.StatusQuery,
                ThreadId: null,
                Payload: JsonSerializer.SerializeToElement(new { }),
                Timestamp: DateTimeOffset.UtcNow);

            var response = await proxy.ReceiveAsync(probe, ct);
            if (response is null || response.Payload.ValueKind != JsonValueKind.Object)
            {
                return new ChildStatusResult("unknown");
            }

            return MapStatusPayload(target, response.Payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to probe status for child {Target}; reporting 'unknown'.",
                target);
            return new ChildStatusResult("unknown");
        }
    }

    private static ChildStatusResult MapStatusPayload(Address target, JsonElement payload)
    {
        if (!payload.TryGetProperty("Status", out var statusElement) ||
            statusElement.ValueKind != JsonValueKind.String)
        {
            return new ChildStatusResult("unknown");
        }

        var raw = statusElement.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ChildStatusResult("unknown");
        }

        var isUnit = string.Equals(target.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase);
        var status = isUnit ? MapLifecycleStatus(raw) : MapAgentStatus(raw);

        string? busyOnThread = null;
        if (status == "busy")
        {
            // #2076 / ADR-0030 §3 §44: AgentActor's StatusQuery payload
            // carries a per-thread ThreadDepths map under concurrent
            // threads (one entry per active thread, value = queue depth).
            // The orchestration probe surfaces a single representative
            // thread id for "busy on what?"; we pick the first entry,
            // which is sufficient for the closed schema's BusyOnThread
            // field. UnitActor does not advertise per-thread depth on
            // its status payload, so the field stays null there.
            if (payload.TryGetProperty("ThreadDepths", out var depthsElement) &&
                depthsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in depthsElement.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(entry.Name))
                    {
                        busyOnThread = entry.Name;
                        break;
                    }
                }
            }
        }

        return new ChildStatusResult(status, LastActivityAt: null, BusyOnThread: busyOnThread);
    }

    private static string MapAgentStatus(string raw) => raw switch
    {
        "Idle" => "ready",
        "Active" => "busy",
        _ => "unknown",
    };

    private static string MapLifecycleStatus(string raw) => raw switch
    {
        "Stopped" => "stopped",
        "Running" => "ready",
        "Starting" => "busy",
        "Stopping" => "busy",
        "Validating" => "busy",
        "Error" => "error",
        "Draft" => "stopped",
        _ => "unknown",
    };

    private static string ResolveKind(Address address) =>
        string.Equals(address.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase)
            ? "unit"
            : "agent";

    private static bool AddressEquals(Address left, Address right) =>
        left.Id == right.Id &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase);
}
