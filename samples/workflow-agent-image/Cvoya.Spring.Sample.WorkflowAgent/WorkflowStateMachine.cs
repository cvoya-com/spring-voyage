// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.WorkflowAgent;

using Cvoya.Spring.AgentSdk;

public static class WorkflowStateMachine
{
    public static async Task<string> RunAsync(
        IOrchestrationClient client,
        string threadId,
        string inboundMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(inboundMessage);

        var child0 = Environment.GetEnvironmentVariable("SPRING_CHILD_0");
        var child1 = Environment.GetEnvironmentVariable("SPRING_CHILD_1");

        if (string.IsNullOrWhiteSpace(child0) || string.IsNullOrWhiteSpace(child1))
        {
            throw new InvalidOperationException("SPRING_CHILD_0 and SPRING_CHILD_1 must be set");
        }

        var target = inboundMessage.Contains("code", StringComparison.OrdinalIgnoreCase)
            ? child0
            : child1;

        // ADR-0049 — delegate_to is a one-way delivery: the ack confirms the
        // message reached the target's mailbox, it does not carry the
        // target's work product. A response, if any, arrives later as a
        // separate one-way message on the thread.
        var ack = await client.DelegateAsync(
            threadId,
            target,
            inboundMessage,
            cancellationToken);

        return ack.Delivered
            ? $"Delegated to {ack.Target} (message {ack.MessageId})."
            : $"Delegation to {ack.Target} was not accepted.";
    }
}
