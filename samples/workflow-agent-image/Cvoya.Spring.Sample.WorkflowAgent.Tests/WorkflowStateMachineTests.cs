// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.WorkflowAgent.Tests;

using Cvoya.Spring.AgentSdk;

using NSubstitute;

using Shouldly;

using Xunit;

public class WorkflowStateMachineTests
{
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    [Fact]
    public async Task RunAsync_CodeMessage_DelegatesToChild0()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var _ = new EnvironmentScope(("SPRING_CHILD_0", "child-0"), ("SPRING_CHILD_1", "child-1"));
            var client = Substitute.For<IOrchestrationClient>();
            client.DelegateAsync("t1", "child-0", "write some code", Arg.Any<CancellationToken>())
                .Returns(new DelegateResponse(true, "msg-1", "child-0", "t1"));

            var result = await WorkflowStateMachine.RunAsync(
                client,
                "t1",
                "write some code",
                TestContext.Current.CancellationToken);

            // ADR-0049 — RunAsync reports the delivery acknowledgement.
            result.ShouldContain("Delegated to child-0");
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    [Fact]
    public async Task RunAsync_NonCodeMessage_DelegatesToChild1()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var _ = new EnvironmentScope(("SPRING_CHILD_0", "child-0"), ("SPRING_CHILD_1", "child-1"));
            var client = Substitute.For<IOrchestrationClient>();
            client.DelegateAsync("t1", "child-1", "draft a plan", Arg.Any<CancellationToken>())
                .Returns(new DelegateResponse(true, "msg-2", "child-1", "t1"));

            var result = await WorkflowStateMachine.RunAsync(
                client,
                "t1",
                "draft a plan",
                TestContext.Current.CancellationToken);

            // ADR-0049 — RunAsync reports the delivery acknowledgement.
            result.ShouldContain("Delegated to child-1");
        }
        finally
        {
            EnvironmentLock.Release();
        }
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
