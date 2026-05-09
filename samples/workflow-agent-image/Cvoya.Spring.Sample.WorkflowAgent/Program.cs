// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.WorkflowAgent;

using Cvoya.Spring.AgentSdk;

public static class Program
{
    public static async Task Main()
    {
        var callbackClient = SpringAgent.FromEnvironment();
        var inboundMessage = await Console.In.ReadToEndAsync();
        var threadId = Environment.GetEnvironmentVariable("SPRING_THREAD_ID")
            ?? throw new InvalidOperationException("SPRING_THREAD_ID env var is required");

        var result = await WorkflowStateMachine.RunAsync(
            callbackClient,
            threadId,
            inboundMessage);

        Console.WriteLine(result);
    }
}
