// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Diagnostics;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="A2AReadinessProbe"/> and
/// <see cref="A2AReadinessFailureFactory"/> — the shared readiness-wait loop
/// that every native-A2A cold-start path delegates to. These pin the #3085
/// hardening: a failing launch fast-fails with a specific, actionable error
/// instead of burning the whole readiness window on a generic timeout.
/// </summary>
public class A2AReadinessProbeTests
{
    private const string ContainerId = "agent-container-1";
    private const string AgentCardUri = "http://localhost:8999/.well-known/agent.json";

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task WaitAsync_EndpointReady_ReturnsSuccess()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await A2AReadinessProbe.WaitAsync(
            runtime, ContainerId, AgentCardUri,
            timeout: TimeSpan.FromSeconds(5),
            probeInterval: ProbeInterval,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        result.Ready.ShouldBeTrue();
        result.Kind.ShouldBeNull();
        // Never had to inspect container state — it answered on the first probe.
        await runtime.DidNotReceive().GetContainerStateAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitAsync_ContainerExitsDuringWait_FastFailsWithExitCodeAndLogs()
    {
        // The container never serves the agent card and has already exited
        // (exit 1) — the canonical #3083 shape. The wait must fail fast,
        // surfacing the exit code + the crash logs, well before the timeout.
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns(false);
        runtime.GetContainerStateAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerRunState(IsRunning: false, ExitCode: 1, Status: "exited"));
        runtime.GetLogsAsync(ContainerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("Traceback (most recent call last):\nModuleNotFoundError: No module named orchestrator");

        var sw = Stopwatch.StartNew();
        var result = await A2AReadinessProbe.WaitAsync(
            runtime, ContainerId, AgentCardUri,
            // A long window: if the fast-fail regressed, the test would hang
            // ~30 s and the assertion below would still catch the slowness.
            timeout: TimeSpan.FromSeconds(30),
            probeInterval: ProbeInterval,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        sw.Stop();

        result.Ready.ShouldBeFalse();
        result.Kind.ShouldBe(A2AReadinessFailureKind.ContainerExited);
        result.Detail!.ShouldContain("exited with code 1");
        result.Detail!.ShouldContain("No module named orchestrator");
        // Fast-fail: nowhere near the 30 s window.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAsync_ContainerExitsWithNoLogs_StillFastFailsWithExitClause()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns(false);
        runtime.GetContainerStateAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerRunState(IsRunning: false, ExitCode: 137, Status: "exited"));
        runtime.GetLogsAsync(ContainerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var result = await A2AReadinessProbe.WaitAsync(
            runtime, ContainerId, AgentCardUri,
            timeout: TimeSpan.FromSeconds(30),
            probeInterval: ProbeInterval,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        result.Ready.ShouldBeFalse();
        result.Kind.ShouldBe(A2AReadinessFailureKind.ContainerExited);
        result.Detail!.ShouldContain("exited with code 137");
        result.Detail!.ShouldContain("no container logs");
    }

    [Fact]
    public async Task WaitAsync_LogReadThrows_StillFastFailsOnExit()
    {
        // A `--rm` container can be reclaimed between exit and the logs read.
        // The exit code alone is still useful — the wait must not mask it.
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns(false);
        runtime.GetContainerStateAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerRunState(IsRunning: false, ExitCode: 2, Status: "exited"));
        runtime.GetLogsAsync(ContainerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("container gone"));

        var result = await A2AReadinessProbe.WaitAsync(
            runtime, ContainerId, AgentCardUri,
            timeout: TimeSpan.FromSeconds(30),
            probeInterval: ProbeInterval,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        result.Ready.ShouldBeFalse();
        result.Kind.ShouldBe(A2AReadinessFailureKind.ContainerExited);
        result.Detail!.ShouldContain("exited with code 2");
    }

    [Fact]
    public async Task WaitAsync_ProbeToolMissing_FastFailsWithProbeToolMissingKind()
    {
        // The probe binary (curl) is missing from the image — a permanent
        // defect surfaced as ContainerProbeToolMissingException. The wait must
        // fast-fail without looping or inspecting container state.
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw ContainerProbeToolMissingException.ForCurl(
                image: "byoi:1", stderr: "exec: \"curl\": executable file not found"));

        var result = await A2AReadinessProbe.WaitAsync(
            runtime, ContainerId, AgentCardUri,
            timeout: TimeSpan.FromSeconds(30),
            probeInterval: ProbeInterval,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        result.Ready.ShouldBeFalse();
        result.Kind.ShouldBe(A2AReadinessFailureKind.ProbeToolMissing);
        result.Detail!.ShouldContain("curl");
        // Permanent failure — no state inspection needed.
        await runtime.DidNotReceive().GetContainerStateAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitAsync_RunningButNeverReady_TimesOutWithTimeoutKind()
    {
        // The container stays up but never serves the agent card. With no exit
        // and no missing tool, the only terminal condition is the timeout.
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns(false);
        runtime.GetContainerStateAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerRunState(IsRunning: true, ExitCode: null, Status: "running"));

        var result = await A2AReadinessProbe.WaitAsync(
            runtime, ContainerId, AgentCardUri,
            timeout: TimeSpan.FromMilliseconds(40),
            probeInterval: ProbeInterval,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        result.Ready.ShouldBeFalse();
        result.Kind.ShouldBe(A2AReadinessFailureKind.Timeout);
        result.Detail!.ShouldContain("did not become ready");
    }

    [Fact]
    public async Task WaitAsync_StateReadFails_FallsBackToTimeout()
    {
        // If container-state inspection itself errors, the wait must not fail
        // the launch on that — the probe loop + timeout remain the backstop.
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns(false);
        runtime.GetContainerStateAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns<ContainerRunState>(_ => throw new InvalidOperationException("inspect failed"));

        var result = await A2AReadinessProbe.WaitAsync(
            runtime, ContainerId, AgentCardUri,
            timeout: TimeSpan.FromMilliseconds(40),
            probeInterval: ProbeInterval,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        result.Ready.ShouldBeFalse();
        result.Kind.ShouldBe(A2AReadinessFailureKind.Timeout);
    }

    [Fact]
    public async Task WaitAsync_OuterCancellation_Propagates()
    {
        var runtime = Substitute.For<IContainerRuntime>();
        runtime.ProbeContainerHttpAsync(ContainerId, AgentCardUri, Arg.Any<CancellationToken>())
            .Returns(false);
        runtime.GetContainerStateAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerRunState(IsRunning: true, ExitCode: null, Status: "running"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await A2AReadinessProbe.WaitAsync(
                runtime, ContainerId, AgentCardUri,
                timeout: TimeSpan.FromSeconds(30),
                probeInterval: ProbeInterval,
                NullLogger.Instance,
                cts.Token));
    }

    // ── A2AReadinessFailureFactory tests ─────────────────────────────────────

    [Fact]
    public void Build_ContainerExited_StampsExitedCodeAndKeepsDetail()
    {
        var result = A2AReadinessResult.Fail(
            A2AReadinessFailureKind.ContainerExited,
            "the container exited with code 1 before the A2A endpoint became ready. Last container logs:\nboom");

        var ex = A2AReadinessFailureFactory.Build("Persistent agent 'abc'", result);

        ex.ShouldBeOfType<SpringException>();
        ex.Message.ShouldContain("Persistent agent 'abc'");
        ex.Message.ShouldContain("failed to launch");
        ex.Message.ShouldContain("exited with code 1");
        ex.Message.ShouldContain("boom");
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe(A2AReadinessFailureFactory.ContainerExitedCode);
    }

    [Fact]
    public void Build_ProbeToolMissing_StampsProbeToolMissingCode()
    {
        var result = A2AReadinessResult.Fail(
            A2AReadinessFailureKind.ProbeToolMissing,
            "Image 'byoi:1' is missing `curl`, which the platform readiness probe requires.");

        var ex = A2AReadinessFailureFactory.Build("Ephemeral agent 'abc'", result);

        ex.Message.ShouldContain("curl");
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe(ContainerProbeToolMissingException.Code);
    }

    [Fact]
    public void Build_Timeout_KeepsHistoricalWordingAndStampsNotReady()
    {
        var result = A2AReadinessResult.Fail(
            A2AReadinessFailureKind.Timeout, "did not become ready within 00:01:00");

        var ex = A2AReadinessFailureFactory.Build("Persistent agent 'abc'", result);

        // The historical substring other surfaces (CLI, portal) match on.
        ex.Message.ShouldContain("did not become ready");
        ex.Data[SpringException.IssueCodeDataKey].ShouldBe(A2AReadinessFailureFactory.NotReadyCode);
    }
}
