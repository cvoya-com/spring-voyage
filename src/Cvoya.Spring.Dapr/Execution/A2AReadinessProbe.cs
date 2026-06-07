// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// Why a readiness wait stopped. The cold-start dispatch paths translate this
/// into the operator-facing failure message.
/// </summary>
internal enum A2AReadinessFailureKind
{
    /// <summary>Probe never answered 2xx before the readiness window elapsed.</summary>
    Timeout,

    /// <summary>The workload container exited before becoming ready (#3085 gap 3).</summary>
    ContainerExited,

    /// <summary>The image ships no probe binary (<c>curl</c>) (#3085 gap 1).</summary>
    ProbeToolMissing,
}

/// <summary>
/// Outcome of <see cref="A2AReadinessProbe.WaitAsync"/>: either the endpoint
/// became ready, or it did not — with a specific reason and an actionable
/// detail string the caller surfaces verbatim.
/// </summary>
/// <param name="Ready">Whether the A2A Agent Card endpoint answered 2xx.</param>
/// <param name="Kind">The failure kind when <see cref="Ready"/> is <c>false</c>; <c>null</c> on success.</param>
/// <param name="Detail">
/// A human-readable failure detail (crash logs, missing-tool message, …) ready
/// to embed in the operator-facing exception. <c>null</c> on success.
/// </param>
internal readonly record struct A2AReadinessResult(
    bool Ready,
    A2AReadinessFailureKind? Kind,
    string? Detail)
{
    internal static A2AReadinessResult Success { get; } = new(Ready: true, Kind: null, Detail: null);

    internal static A2AReadinessResult Fail(A2AReadinessFailureKind kind, string detail)
        => new(Ready: false, Kind: kind, Detail: detail);
}

/// <summary>
/// Maps a non-ready <see cref="A2AReadinessResult"/> into the operator-facing
/// <see cref="SpringException"/>, stamping a stable issue code so the failure
/// is attributable. Shared by every cold-start path so the deploy and
/// auto-start surfaces produce the same diagnosis. The timeout case keeps the
/// historical "did not become ready within …" wording; the exit / missing-tool
/// cases carry the specific diagnosis (#3085).
/// </summary>
internal static class A2AReadinessFailureFactory
{
    /// <summary>Issue code stamped when the container exited before becoming ready.</summary>
    internal const string ContainerExitedCode = "ContainerExitedBeforeReady";

    /// <summary>Issue code stamped on a plain readiness timeout.</summary>
    internal const string NotReadyCode = "AgentNotReady";

    /// <summary>
    /// Builds the failure exception for a non-ready outcome.
    /// </summary>
    /// <param name="subject">The agent description that prefixes the message (e.g. <c>Persistent agent 'abc'</c>).</param>
    /// <param name="readiness">The non-ready readiness outcome.</param>
    internal static SpringException Build(string subject, A2AReadinessResult readiness)
    {
        var detail = readiness.Detail ?? "did not become ready";
        return readiness.Kind switch
        {
            A2AReadinessFailureKind.ProbeToolMissing => new SpringException(
                    $"{subject} failed to launch: {detail}")
                .WithIssue(ContainerProbeToolMissingException.Code, ContainerProbeToolMissingException.IssueSource),
            A2AReadinessFailureKind.ContainerExited => new SpringException(
                    $"{subject} failed to launch: {detail}")
                .WithIssue(ContainerExitedCode, "runtime"),
            _ => new SpringException($"{subject} {detail}.")
                .WithIssue(NotReadyCode, "runtime"),
        };
    }
}

/// <summary>
/// Shared readiness-wait loop used by every native-A2A cold-start path
/// (<see cref="A2AExecutionDispatcher"/> ephemeral + persistent, and
/// <see cref="PersistentAgentRegistry"/> deploy) so they cannot drift on what
/// "ready" means or on how a failing launch is diagnosed.
/// </summary>
/// <remarks>
/// The hardening from #3085 lives here: instead of polling
/// <see cref="IContainerRuntime.ProbeContainerHttpAsync"/> against a corpse for
/// the full window and reporting a generic
/// "<c>did not become ready within 00:01:00</c>", the loop
/// <list type="bullet">
///   <item>fast-fails the moment the container exits, surfacing the tail of
///   <see cref="IContainerRuntime.GetLogsAsync"/> (e.g.
///   <c>No module named orchestrator</c>); and</item>
///   <item>fast-fails when the probe binary (<c>curl</c>) is absent from the
///   image, with an actionable message naming the missing dependency.</item>
/// </list>
/// </remarks>
internal static class A2AReadinessProbe
{
    /// <summary>
    /// Number of log lines to attach to a fast-fail-on-exit failure detail.
    /// Enough to carry a Python traceback's final frames without flooding the
    /// dispatch error.
    /// </summary>
    internal const int CrashLogTailLines = 40;

    /// <summary>
    /// Polls the agent container's A2A Agent Card endpoint until it answers
    /// 2xx, the container exits, the probe tool is found missing, or the
    /// timeout elapses.
    /// </summary>
    /// <param name="runtime">The container runtime backing the probe + state + logs reads.</param>
    /// <param name="containerId">The workload container's identifier.</param>
    /// <param name="agentCardUri">The fully-qualified <c>/.well-known/agent.json</c> URI to probe.</param>
    /// <param name="timeout">The readiness window.</param>
    /// <param name="probeInterval">Delay between probe attempts.</param>
    /// <param name="logger">Logger for per-attempt diagnostics.</param>
    /// <param name="cancellationToken">Outer cancellation token (propagated when cancelled by the caller).</param>
    /// <returns>The readiness outcome.</returns>
    internal static async Task<A2AReadinessResult> WaitAsync(
        IContainerRuntime runtime,
        string containerId,
        string agentCardUri,
        TimeSpan timeout,
        TimeSpan probeInterval,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var attempts = 0;
        Exception? lastException = null;

        while (!cts.Token.IsCancellationRequested)
        {
            attempts++;
            try
            {
                if (await runtime.ProbeContainerHttpAsync(containerId, agentCardUri, cts.Token))
                {
                    logger.LogDebug(
                        "A2A endpoint {Uri} ready after {Attempts} attempt(s) (container {ContainerId})",
                        agentCardUri, attempts, containerId);
                    return A2AReadinessResult.Success;
                }
            }
            catch (ContainerProbeToolMissingException ex)
            {
                // #3085 gap 1: permanent image defect — no point polling.
                logger.LogError(
                    "A2A readiness probe for container {ContainerId} cannot run: {Message}",
                    containerId, ex.Message);
                return A2AReadinessResult.Fail(A2AReadinessFailureKind.ProbeToolMissing, ex.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Internal CancelAfter fired mid-probe — fall through to the
                // exit-check / timeout handling below.
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                logger.LogDebug(
                    "A2A readiness probe attempt {Attempt} for {Uri} failed: {Reason}",
                    attempts, agentCardUri, ex.Message);
            }

            // #3085 gap 3: before sleeping for another interval, check whether
            // the container has already exited. A crashed container will never
            // become ready, so fail fast with its exit code + log tail instead
            // of burning the rest of the window.
            var exitFailure = await CheckForExitAsync(runtime, containerId, logger, cts.Token);
            if (exitFailure is { } failure)
            {
                return failure;
            }

            try
            {
                await Task.Delay(probeInterval, cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        // Honour outer cancellation by surfacing it to the caller.
        cancellationToken.ThrowIfCancellationRequested();

        // One last exit-check: the container may have died exactly as the
        // window closed, which is a more useful diagnosis than a bare timeout.
        var finalExit = await CheckForExitAsync(runtime, containerId, logger, CancellationToken.None);
        if (finalExit is { } finalFailure)
        {
            return finalFailure;
        }

        logger.LogWarning(
            "A2A endpoint {Uri} did not become ready after {Attempts} attempt(s) within {Timeout} (container {ContainerId}). Last error: {LastError}",
            agentCardUri, attempts, timeout, containerId, lastException?.Message ?? "(none)");
        return A2AReadinessResult.Fail(
            A2AReadinessFailureKind.Timeout,
            $"did not become ready within {timeout}");
    }

    /// <summary>
    /// Returns a fast-fail result when the container has exited, attaching the
    /// exit code and the tail of its logs; <c>null</c> while it is still
    /// running (or its state cannot be read, in which case the caller keeps
    /// polling and the timeout remains the backstop).
    /// </summary>
    private static async Task<A2AReadinessResult?> CheckForExitAsync(
        IContainerRuntime runtime,
        string containerId,
        ILogger logger,
        CancellationToken ct)
    {
        ContainerRunState? state;
        try
        {
            state = await runtime.GetContainerStateAsync(containerId, ct);
        }
        catch (OperationCanceledException)
        {
            // The state read itself was cancelled — let the loop's own
            // cancellation handling decide what to do.
            return null;
        }
        catch (Exception ex)
        {
            // Couldn't read state — don't fail the wait on this; the probe
            // loop + timeout remain the backstop.
            logger.LogDebug(
                ex,
                "Could not read container state for {ContainerId} during readiness wait: {Reason}",
                containerId, ex.Message);
            return null;
        }

        // No state available (runtime could not report one) — keep polling;
        // the timeout remains the backstop.
        if (state is null || state.IsRunning)
        {
            return null;
        }

        var logsTail = await SafeReadLogsTailAsync(runtime, containerId, logger);
        var exitClause = state.ExitCode is { } code
            ? $"exited with code {code}"
            : $"is no longer running (status: {state.Status})";

        var detail = string.IsNullOrWhiteSpace(logsTail)
            ? $"the container {exitClause} before the A2A endpoint became ready (no container logs were captured)"
            : $"the container {exitClause} before the A2A endpoint became ready. Last container logs:\n{logsTail}";

        logger.LogError(
            "Container {ContainerId} {ExitClause} during readiness wait. Logs tail:\n{Logs}",
            containerId, exitClause, logsTail);

        return A2AReadinessResult.Fail(A2AReadinessFailureKind.ContainerExited, detail);
    }

    private static async Task<string> SafeReadLogsTailAsync(
        IContainerRuntime runtime,
        string containerId,
        ILogger logger)
    {
        try
        {
            var logs = await runtime.GetLogsAsync(containerId, CrashLogTailLines, CancellationToken.None);
            return logs.Trim();
        }
        catch (Exception ex)
        {
            // A `--rm` container may already be gone; the exit code alone is
            // still useful, so log + return empty rather than masking it.
            logger.LogDebug(
                ex,
                "Could not read logs for exited container {ContainerId}: {Reason}",
                containerId, ex.Message);
            return string.Empty;
        }
    }
}
