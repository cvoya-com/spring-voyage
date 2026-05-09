// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.WorkflowAgent.Tests;

using System.Reflection;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

public class SpringAgentTests
{
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    [Fact]
    public async Task FromEnvironment_InboundCallbackToken_PrefersPerMessageToken()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var _ = new EnvironmentScope(
                ("SPRING_CALLBACK_URL", "http://localhost:5104"),
                ("SPRING_CALLBACK_TOKEN", "stale-launch-token"));

            var client = SpringAgent.FromEnvironment(
                """
                {
                  "metadata": {
                    "callbackToken": "fresh-message-token"
                  },
                  "parts": [
                    { "kind": "text", "text": "turn two" }
                  ]
                }
                """);

            ReadCallbackToken(client).ShouldBe("fresh-message-token");
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    [Fact]
    public async Task FromEnvironment_NoInboundCallbackToken_UsesEnvironmentToken()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var _ = new EnvironmentScope(
                ("SPRING_CALLBACK_URL", "http://localhost:5104"),
                ("SPRING_CALLBACK_TOKEN", "launch-token"));

            var client = SpringAgent.FromEnvironment("plain text payload");

            ReadCallbackToken(client).ShouldBe("launch-token");
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    private static string ReadCallbackToken(IOrchestrationClient client)
    {
        client.ShouldBeOfType<OrchestrationClient>();
        var field = typeof(OrchestrationClient).GetField(
            "_callbackToken",
            BindingFlags.Instance | BindingFlags.NonPublic);

        field.ShouldNotBeNull();
        return (string)field!.GetValue(client)!;
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string? Value)[] _previousValues;

        public EnvironmentScope(params (string Name, string Value)[] values)
        {
            _previousValues = values
                .Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))
                .ToArray();

            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
