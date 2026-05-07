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
/// <see cref="MessageRouter"/>, and clearing the active thread slot when
/// the dispatch terminates abnormally.
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
    /// <inheritdoc />
    public async Task RunDispatchAsync(
        string agentId,
        Message message,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> clearActiveConversation,
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
                // Even when the dispatcher returns nothing to route, the turn
                // is over from the actor's perspective. The active-thread slot
                // must be released so the next message can be dispatched
                // instead of being queued forever as pending. See the success
                // branch below for the same rationale.
                await clearActiveConversation("dispatch returned no response");
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
                // BEFORE clearing the active thread so the response is
                // ordered correctly in the thread event log.
                await TryRouteResponseAsync(agentId, response, message.ThreadId, cancellationToken);

                await clearActiveConversation($"dispatch exit code {failure.ExitCode}");
                return;
            }

            await TryRouteResponseAsync(agentId, response, message.ThreadId, cancellationToken);

            // The successful turn is complete: the dispatcher returned a
            // response and we routed it back to the original sender. The
            // actor's ActiveConversation slot must be released so the next
            // message dispatched to this agent can run, instead of being
            // queued behind a thread the actor has actually finished with.
            // The error/cancel branches above call clearActiveConversation
            // for the same reason — without an explicit clear here, an
            // agent that ever completes a turn successfully looks bricked
            // to every later sender, including the very human it just
            // replied to. Discovered while debugging an agent stuck in
            // Active state after a working dispatch (Status=Active,
            // PendingConversationCount=0): every follow-up send was either
            // appended to the dead active channel (Case 2 in
            // HandleDomainMessageAsync) or queued as pending (Case 3) and
            // never dispatched.
            await clearActiveConversation("dispatch completed");
        }
        catch (OperationCanceledException)
        {
            // A cancelled dispatch leaves the active-thread slot
            // pointing at a dead turn. Without clearing it, the actor
            // refuses every subsequent message in any other thread
            // (Case 3 in HandleDomainMessageAsync queues them as pending
            // forever) and the agent looks bricked from the user's
            // perspective. The non-zero exit and generic-exception
            // branches above already call clearActiveConversation for
            // exactly this reason; the cancel branch must too.
            // Discovered post-Stage-2 cutover (#1063 / #522 follow-up):
            // a worker-side HttpClient timeout surfaced as
            // OperationCanceledException, the actor logged it but kept
            // the thread marked Active, and every subsequent
            // user message was queued as pending and never dispatched.
            logger.LogInformation(
                "Dispatch cancelled for actor {ActorId} thread {ThreadId}.",
                agentId, message.ThreadId);

            await clearActiveConversation("dispatch cancelled");
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

            await clearActiveConversation($"dispatch exception: {ex.GetType().Name}");
        }
    }

    private async Task TryRouteResponseAsync(
        string agentId,
        Message response,
        string? threadId,
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
            }
        }
        catch (Exception routeEx)
        {
            logger.LogWarning(routeEx,
                "Routing dispatcher response failed for thread {ThreadId}.",
                threadId);
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