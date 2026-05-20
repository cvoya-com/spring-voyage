// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SpringAgent.RunWithResponseDisciplineAsync"/>
/// (#2493). Tests use the internal overload so we can inject a fake
/// <see cref="IOrchestrationClient"/> and inspect the safety-net
/// behaviour without env vars or HTTP.
/// </summary>
public class SpringAgentSafetyNetTests
{
    private sealed class RecordingOrchestrationClient : IOrchestrationClient
    {
        public List<(string ThreadId, string Result)> Posts { get; } = new();

        public Task PostResultAsync(string threadId, string result, CancellationToken cancellationToken = default)
        {
            Posts.Add((threadId, result));
            return Task.CompletedTask;
        }

        public Task<DelegateResponse> DelegateAsync(string threadId, string targetUnitId, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new DelegateResponse(true, string.Empty, string.Empty, string.Empty));

        public Task<FanoutResponse> FanoutAsync(string threadId, IReadOnlyList<string> targetUnitIds, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new FanoutResponse(string.Empty, string.Empty, Array.Empty<FanoutDelivery>()));
    }

    private static TelemetryClient NewDisabledTelemetryClient()
    {
        // No endpoint → disabled emitter; emit_* calls are no-ops.
        return new TelemetryClient(endpoint: null);
    }

    [Fact]
    public async Task HandlerThatPostsResult_SkipsSafetyNet()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var orchestration = new RecordingOrchestrationClient();
        using var telemetry = NewDisabledTelemetryClient();

        await SpringAgent.RunWithResponseDisciplineAsync(
            threadId,
            orchestration,
            telemetry,
            async (bundle, ct) =>
            {
                await bundle.Orchestration.PostResultAsync(threadId, "hello", ct);
            },
            disposeTelemetry: false,
            cancellationToken: CancellationToken.None);

        orchestration.Posts.Count.ShouldBe(1);
        orchestration.Posts[0].Result.ShouldBe("hello");
    }

    [Fact]
    public async Task HandlerThatReturnsWithoutPosting_TriggersSafetyNet()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var orchestration = new RecordingOrchestrationClient();
        using var telemetry = NewDisabledTelemetryClient();

        await SpringAgent.RunWithResponseDisciplineAsync(
            threadId,
            orchestration,
            telemetry,
            handler: (bundle, ct) => Task.CompletedTask,
            disposeTelemetry: false,
            cancellationToken: CancellationToken.None);

        // The safety net posts the stock reply on the user's behalf.
        orchestration.Posts.Count.ShouldBe(1);
        orchestration.Posts[0].Result.ShouldBe(SpringAgent.SafetyNetReply);
    }

    [Fact]
    public async Task HandlerThatThrows_StillPostsSafetyNetReplyAndRethrows()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var orchestration = new RecordingOrchestrationClient();
        using var telemetry = NewDisabledTelemetryClient();

        var ex = await Should.ThrowAsync<SpringAgentHandlerException>(
            () => SpringAgent.RunWithResponseDisciplineAsync(
                threadId,
                orchestration,
                telemetry,
                handler: (bundle, ct) => throw new InvalidOperationException("boom"),
                disposeTelemetry: false,
                cancellationToken: CancellationToken.None));

        ex.InnerException.ShouldBeOfType<InvalidOperationException>();

        // The safety-net reply still ships even though the handler threw.
        orchestration.Posts.Count.ShouldBe(1);
        orchestration.Posts[0].Result.ShouldBe(SpringAgent.SafetyNetReply);
    }

    [Fact]
    public async Task HandlerDelegatingDoesNotSatisfyDiscipline()
    {
        // Delegation alone is not a final reply to the requester —
        // the safety net must still fire.
        var threadId = Guid.NewGuid().ToString("D");
        var orchestration = new RecordingOrchestrationClient();
        using var telemetry = NewDisabledTelemetryClient();

        await SpringAgent.RunWithResponseDisciplineAsync(
            threadId,
            orchestration,
            telemetry,
            async (bundle, ct) =>
            {
                await bundle.Orchestration.DelegateAsync(threadId, "unit:abc", "do something", ct);
            },
            disposeTelemetry: false,
            cancellationToken: CancellationToken.None);

        orchestration.Posts.Count.ShouldBe(1);
        orchestration.Posts[0].Result.ShouldBe(SpringAgent.SafetyNetReply);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var orchestration = new RecordingOrchestrationClient();
        using var telemetry = NewDisabledTelemetryClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => SpringAgent.RunWithResponseDisciplineAsync(
                threadId,
                orchestration,
                telemetry,
                handler: async (bundle, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                },
                disposeTelemetry: false,
                cancellationToken: cts.Token));
    }
}
