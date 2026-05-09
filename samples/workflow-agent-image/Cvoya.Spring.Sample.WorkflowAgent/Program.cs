// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.WorkflowAgent;

using Cvoya.Spring.AgentSdk;

public static class Program
{
    public static async Task Main()
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            var callbackClient = SpringAgent.FromEnvironment();
            var inboundMessage = await Console.In.ReadToEndAsync(cancellation.Token);
            var threadId = Environment.GetEnvironmentVariable("SPRING_THREAD_ID")
                ?? throw new InvalidOperationException("SPRING_THREAD_ID env var is required");

            var result = await WorkflowStateMachine.RunAsync(
                callbackClient,
                threadId,
                inboundMessage,
                cancellation.Token);

            Console.WriteLine(result);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
