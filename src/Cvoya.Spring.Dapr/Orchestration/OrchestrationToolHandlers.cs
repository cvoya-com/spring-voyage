// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

public class OrchestrationToolHandlers(
    IActorProxyFactory actorProxyFactory,
    IAgentProxyResolver agentProxyResolver,
    OrchestrationDepthCounter depthCounter,
    ILogger<OrchestrationToolHandlers> logger)
{
    public async Task<Address[]> HandleListChildrenAsync(
        Address caller,
        Guid threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        EnsureUnitCaller(caller);

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

        return await SendToTargetAsync(caller, target, message, ct);
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
        return await Task.WhenAll(tasks);
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

        // ADR-0039 / #1826 v0.1 default: child status has no concrete source yet.
        return "unknown";
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