// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json;

/// <summary>
/// Entry point for runtime images running inside Spring Voyage.
/// </summary>
public static class SpringAgent
{
    /// <summary>
    /// Stock reply text the response-discipline safety net synthesizes
    /// when the user's delegate exits without calling
    /// <see cref="IMessagingClient.PostResultAsync"/>. Issue #2493.
    /// Kept terse and neutral so it's not mistaken for the agent's real
    /// voice; the activity log carries the structural detail.
    /// </summary>
    public const string SafetyNetReply =
        "Turn completed without an explicit final response. " +
        "See the activity log for details.";

    /// <summary>
    /// Creates a MessagingClient configured from the standard environment variables.
    /// Throws MissingCallbackEnvironmentException if SPRING_CALLBACK_URL or
    /// SPRING_CALLBACK_TOKEN are absent.
    /// </summary>
    public static IMessagingClient FromEnvironment() => FromEnvironment(inboundMessageBody: null);

    /// <summary>
    /// Creates a <see cref="SpringAgentBundle"/> exposing both the
    /// messaging client and the telemetry primitives. Use this when
    /// the agent needs to emit progress / tool-call / llm-turn telemetry
    /// alongside posting results back to the dispatcher.
    /// </summary>
    public static SpringAgentBundle FromEnvironmentWithTelemetry(string? inboundMessageBody = null)
    {
        var messaging = FromEnvironment(inboundMessageBody);
        // #3041 Part B: the agent runtime does not read SPRING_THREAD_ID to
        // learn a "thread". A conversation is the participant set on the
        // inbound message, not a platform thread id, so telemetry is built
        // without one; internal turn correlation is server-side audit
        // (ToolResult activity-event CorrelationId), not an agent concern.
        var telemetry = TelemetryClient.FromEnvironment();
        return new SpringAgentBundle(messaging, telemetry);
    }

    /// <summary>
    /// Runs the user's agent delegate with response-discipline
    /// enforcement (issue #2493). If the delegate completes without
    /// calling <see cref="IMessagingClient.PostResultAsync"/>, this
    /// helper:
    /// <list type="number">
    /// <item><description>posts the stock <see cref="SafetyNetReply"/> for the dispatcher thread,</description></item>
    /// <item><description>emits a <c>response_discipline_violation</c> telemetry event,</description></item>
    /// <item><description>logs a warning to stderr.</description></item>
    /// </list>
    /// The synthesized reply still ships even if the delegate threw —
    /// the platform user always sees <i>something</i>.
    /// </summary>
    /// <param name="threadId">
    /// Internal dispatch-binding id the safety-net result post is bound to.
    /// Required. This is platform-managed plumbing, not an agent-facing
    /// "thread" handle (#3041): a delegate identifies its conversation by
    /// the participant set on the inbound message, never by this id.
    /// </param>
    /// <param name="handler">
    /// User delegate. Receives the bundle (messaging + telemetry).
    /// Returning normally without calling <c>PostResultAsync</c> trips
    /// the safety net. Throwing also trips it — the exception is
    /// captured into the violation event but not the synthesized reply.
    /// </param>
    /// <param name="inboundMessageBody">
    /// Optional inbound A2A message body — passed through to
    /// <see cref="FromEnvironment(string?)"/> so the per-message
    /// <c>callbackToken</c> metadata override is honoured.
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole turn.</param>
    public static Task RunWithResponseDisciplineAsync(
        string threadId,
        Func<SpringAgentBundle, CancellationToken, Task> handler,
        string? inboundMessageBody = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(handler);

        var messaging = FromEnvironment(inboundMessageBody);
        var telemetry = TelemetryClient.FromEnvironment(threadId);
        return RunWithResponseDisciplineAsync(
            threadId,
            messaging,
            telemetry,
            handler,
            disposeTelemetry: true,
            cancellationToken);
    }

    /// <summary>
    /// Test-seam overload: same response-discipline contract as the
    /// public overload, but accepts explicit dependencies so tests can
    /// substitute a fake <see cref="IMessagingClient"/> and inspect
    /// the safety-net behaviour without env vars or HTTP. The public
    /// surface is the env-driven overload; this overload is internal so
    /// the test assembly can reach it via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static async Task RunWithResponseDisciplineAsync(
        string threadId,
        IMessagingClient messaging,
        TelemetryClient telemetry,
        Func<SpringAgentBundle, CancellationToken, Task> handler,
        bool disposeTelemetry,
        CancellationToken cancellationToken)
    {
        var tracker = new ResponseDisciplineTrackingClient(messaging);
        var bundle = new SpringAgentBundle(tracker, telemetry);

        Exception? handlerException = null;
        try
        {
            try
            {
                await handler(bundle, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                handlerException = ex;
            }

            if (!tracker.ResultPosted)
            {
                var reason = handlerException is null
                    ? "Handler returned without calling PostResultAsync; synthesised reply emitted by SDK safety net (#2493)."
                    : $"Handler threw {handlerException.GetType().Name} before calling PostResultAsync; synthesised reply emitted by SDK safety net (#2493).";

                Console.Error.WriteLine($"[spring-voyage] Response-discipline violation on thread {threadId}: {reason}");
                telemetry.EmitResponseDisciplineViolation(reason);

                try
                {
                    await tracker.InnerPostResultAsync(threadId, SafetyNetReply, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception postEx)
                {
                    Console.Error.WriteLine(
                        $"[spring-voyage] Safety-net PostResultAsync failed for thread {threadId}: {postEx.Message}");
                }
            }

            if (handlerException is not null)
            {
                throw new SpringAgentHandlerException(
                    "Agent handler raised before completing; safety-net reply was posted to thread.",
                    handlerException);
            }
        }
        finally
        {
            if (disposeTelemetry)
            {
                telemetry.Dispose();
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="MessagingClient"/> from the platform MCP
    /// environment contract (<c>SPRING_MCP_URL</c> / <c>SPRING_MCP_TOKEN</c>).
    /// </summary>
    /// <param name="inboundMessageBody">
    /// Retained for source compatibility. ADR-0054 retired the per-message
    /// <c>message.metadata.callbackToken</c> override — the MCP session token
    /// is minted per turn and revoked on turn-end, so there is nothing to
    /// override. The parameter is ignored.
    /// </param>
    public static IMessagingClient FromEnvironment(string? inboundMessageBody)
    {
        _ = inboundMessageBody;
        return FromMcpEnvironment();
    }

    /// <summary>
    /// Creates a <see cref="MessagingClient"/> from the platform MCP
    /// environment contract (<c>SPRING_MCP_URL</c> / <c>SPRING_MCP_TOKEN</c>).
    /// </summary>
    /// <param name="inboundMessageBody">
    /// Retained for source compatibility. ADR-0054 retired the per-message
    /// callback-token override; the parameter is ignored.
    /// </param>
    public static IMessagingClient FromEnvironment(JsonElement inboundMessageBody)
    {
        _ = inboundMessageBody;
        return FromMcpEnvironment();
    }

    private static IMessagingClient FromMcpEnvironment()
    {
        var mcpUrl = ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.McpUrlEnvVar);
        var mcpToken = ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.McpTokenEnvVar);

        return new MessagingClient(mcpUrl, mcpToken);
    }

    private static string ReadRequiredEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MissingCallbackEnvironmentException(variableName);
        }

        return value;
    }
}
