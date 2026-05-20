// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentDispatchCoordinator"/>.
/// Owns the execution-dispatch concern extracted from <c>AgentActor</c>:
/// invoking the <see cref="IExecutionDispatcher"/>, inspecting the response
/// for a non-zero container exit code, routing the response via
/// <see cref="MessageRouter"/>, and signalling the per-thread dispatch exit
/// so the actor's mailbox can drain remaining queued messages on the same
/// thread or mark the channel idle.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected singleton
/// seams. This makes it safe to register as a singleton and share across all
/// <c>AgentActor</c> instances.
/// </remarks>
public class AgentDispatchCoordinator(
    IExecutionDispatcher executionDispatcher,
    MessageRouter messageRouter,
    ILogger<AgentDispatchCoordinator> logger) : IAgentDispatchCoordinator
{
    /// <summary>
    /// Reason string signalled through <c>onDispatchExit</c> when the
    /// dispatcher returns a null response. Shared with
    /// <see cref="Actors.RuntimeInvocationPath"/> so the lean path can
    /// recognise this exit cause and surface a corresponding "no response"
    /// activity row. Promoted to an internal constant so the coordinator
    /// and consumers stay in lockstep — changing the literal in one place
    /// would silently disable the consumer's recognition logic (#2222).
    /// </summary>
    internal const string DispatchNoResponseReason = "dispatch returned no response";

    /// <inheritdoc />
    public async Task RunDispatchAsync(
        string agentId,
        Message message,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> onDispatchExit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await executionDispatcher.DispatchAsync(message, context, cancellationToken);
            if (response is null)
            {
                logger.LogInformation(
                    "Dispatcher returned no response for thread {ThreadId}; nothing to route.",
                    message.ThreadId);
                // Even when the dispatcher returns nothing to route, the
                // dispatch is over from the actor's perspective. Signal the
                // per-thread exit so the mailbox can drain any messages
                // appended during the dispatch (per-thread FIFO) or mark
                // the channel idle.
                await onDispatchExit(DispatchNoResponseReason);
                return;
            }

            var dispatchExit = TryReadDispatchExit(response);
            if (dispatchExit is { ExitCode: not 0 } failure)
            {
                logger.LogWarning(
                    "Dispatch for actor {ActorId} thread {ThreadId} exited with code {ExitCode}: {StdErrFirstLine}",
                    agentId, message.ThreadId, failure.ExitCode, failure.StdErrFirstLine);

                var details = JsonSerializer.SerializeToElement(new
                {
                    exitCode = failure.ExitCode,
                    stderr = failure.StdErr,
                    agentId,
                    threadId = message.ThreadId,
                });

                await emitActivity(
                    BuildEvent(
                        agentId,
                        message.ThreadId,
                        ActivityEventType.ErrorOccurred,
                        ActivitySeverity.Error,
                        $"Container exit code {failure.ExitCode}: {failure.StdErrFirstLine}",
                        details: details),
                    CancellationToken.None);

                // Best-effort: still surface the failure to the caller so an
                // upstream agent / human sees the error response. We do this
                // BEFORE signalling the exit so the response is ordered
                // correctly in the thread event log.
                await TryRouteResponseAsync(agentId, response, message.ThreadId, emitActivity, cancellationToken);

                await onDispatchExit($"dispatch exit code {failure.ExitCode}");
                return;
            }

            await TryRouteResponseAsync(agentId, response, message.ThreadId, emitActivity, cancellationToken);

            // Per-thread dispatch is complete: the dispatcher returned a
            // response and we routed it back to the original sender. The
            // actor's mailbox drains any messages appended during the
            // dispatch (per-thread FIFO) or marks the channel idle. Other
            // threads on the same agent are unaffected — concurrent
            // threads run independently per ADR-0030 §44.
            await onDispatchExit("dispatch completed");
        }
        catch (OperationCanceledException)
        {
            // A cancelled dispatch leaves the channel mid-drain. Signal
            // the exit so the mailbox doesn't sit Active-but-idle: the
            // actor either drains the next queued message on the same
            // thread or marks the channel idle. Other threads are
            // unaffected (they have their own channels and dispatchers).
            logger.LogInformation(
                "Dispatch cancelled for actor {ActorId} thread {ThreadId}.",
                agentId, message.ThreadId);

            await onDispatchExit("dispatch cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Dispatch failed for actor {ActorId} thread {ThreadId}.",
                agentId, message.ThreadId);

            await emitActivity(
                BuildEvent(
                    agentId,
                    message.ThreadId,
                    ActivityEventType.ErrorOccurred,
                    ActivitySeverity.Error,
                    $"Dispatch failed: {ex.Message}",
                    details: JsonSerializer.SerializeToElement(new
                    {
                        error = ex.Message,
                        agentId,
                        threadId = message.ThreadId,
                    })),
                CancellationToken.None);

            await onDispatchExit($"dispatch exception: {ex.GetType().Name}");
        }
    }

    private async Task TryRouteResponseAsync(
        string agentId,
        Message response,
        string? threadId,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken)
    {
        try
        {
            var routingResult = await messageRouter.RouteAsync(response, cancellationToken);
            if (!routingResult.IsSuccess)
            {
                logger.LogWarning(
                    "Failed to route dispatcher response for thread {ThreadId}: {Error}",
                    threadId, routingResult.Error);

                await emitActivity(
                    BuildEvent(
                        agentId,
                        threadId,
                        ActivityEventType.ErrorOccurred,
                        ActivitySeverity.Error,
                        $"Routing failed: {routingResult.Error}"),
                    CancellationToken.None);
            }
        }
        catch (Exception routeEx)
        {
            logger.LogWarning(routeEx,
                "Routing dispatcher response failed for thread {ThreadId}.",
                threadId);

            await emitActivity(
                BuildEvent(
                    agentId,
                    threadId,
                    ActivityEventType.ErrorOccurred,
                    ActivitySeverity.Error,
                    $"Routing failed: {routeEx.Message}"),
                CancellationToken.None);
        }
    }

    private readonly record struct DispatchExit(int ExitCode, string? StdErr, string StdErrFirstLine);

    private static DispatchExit? TryReadDispatchExit(Message response)
    {
        try
        {
            if (response.Payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!response.Payload.TryGetProperty("ExitCode", out var exitProp) ||
                exitProp.ValueKind != JsonValueKind.Number ||
                !exitProp.TryGetInt32(out var exitCode))
            {
                return null;
            }

            string? stderr = null;
            if (response.Payload.TryGetProperty("Error", out var errProp) &&
                errProp.ValueKind == JsonValueKind.String)
            {
                stderr = errProp.GetString();
            }

            var firstLine = stderr is null
                ? string.Empty
                : stderr.Split('\n', 2)[0].TrimEnd('\r').Trim();

            return new DispatchExit(exitCode, stderr, firstLine);
        }
        catch
        {
            return null;
        }
    }

    private static ActivityEvent BuildEvent(
        string agentId,
        string? correlationId,
        ActivityEventType eventType,
        ActivitySeverity severity,
        string summary,
        JsonElement? details = null)
    {
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Address.For("agent", agentId),
            eventType,
            severity,
            summary,
            details,
            correlationId);
    }
}
