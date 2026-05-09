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

        var response = await client.DelegateAsync(
            threadId,
            target,
            inboundMessage,
            cancellationToken);

        return response.Result;
    }
}
