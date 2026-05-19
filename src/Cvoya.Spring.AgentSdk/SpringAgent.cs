// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json;

/// <summary>
/// Entry point for runtime images running inside Spring Voyage.
/// </summary>
public static class SpringAgent
{
    private const string CallbackTokenPayloadField = "callbackToken";

    /// <summary>
    /// Stock reply text the response-discipline safety net synthesizes
    /// when the user's delegate exits without calling
    /// <see cref="IOrchestrationClient.PostResultAsync"/>. Issue #2493.
    /// Kept terse and neutral so it's not mistaken for the agent's real
    /// voice; the activity log carries the structural detail.
    /// </summary>
    public const string SafetyNetReply =
        "Turn completed without an explicit final response. " +
        "See the activity log for details.";

    /// <summary>
    /// Creates an OrchestrationClient configured from the standard environment variables.
    /// Throws MissingCallbackEnvironmentException if SPRING_CALLBACK_URL or
    /// SPRING_CALLBACK_TOKEN are absent.
    /// </summary>
    public static IOrchestrationClient FromEnvironment() => FromEnvironment(inboundMessageBody: null);

    /// <summary>
    /// Creates a <see cref="SpringAgentBundle"/> exposing both the
    /// orchestration client and the telemetry primitives. Use this when
    /// the agent needs to emit progress / tool-call / llm-turn telemetry
    /// alongside posting results back to the dispatcher.
    /// </summary>
    public static SpringAgentBundle FromEnvironmentWithTelemetry(string? inboundMessageBody = null)
    {
        var orchestration = FromEnvironment(inboundMessageBody);
        var threadId = Environment.GetEnvironmentVariable("SPRING_THREAD_ID");
        var telemetry = TelemetryClient.FromEnvironment(threadId);
        return new SpringAgentBundle(orchestration, telemetry);
    }

    /// <summary>
    /// Runs the user's agent delegate with response-discipline
    /// enforcement (issue #2493). If the delegate completes without
    /// calling <see cref="IOrchestrationClient.PostResultAsync"/>, this
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
    /// Dispatcher thread id. Required: the result post is bound to this
    /// thread. Normally read from <c>SPRING_THREAD_ID</c> at the agent's
    /// entry point.
    /// </param>
    /// <param name="handler">
    /// User delegate. Receives the bundle (orchestration + telemetry).
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

        var orchestration = FromEnvironment(inboundMessageBody);
        var telemetry = TelemetryClient.FromEnvironment(threadId);
        return RunWithResponseDisciplineAsync(
            threadId,
            orchestration,
            telemetry,
            handler,
            disposeTelemetry: true,
            cancellationToken);
    }

    /// <summary>
    /// Test-seam overload: same response-discipline contract as the
    /// public overload, but accepts explicit dependencies so tests can
    /// substitute a fake <see cref="IOrchestrationClient"/> and inspect
    /// the safety-net behaviour without env vars or HTTP. The public
    /// surface is the env-driven overload; this overload is internal so
    /// the test assembly can reach it via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static async Task RunWithResponseDisciplineAsync(
        string threadId,
        IOrchestrationClient orchestration,
        TelemetryClient telemetry,
        Func<SpringAgentBundle, CancellationToken, Task> handler,
        bool disposeTelemetry,
        CancellationToken cancellationToken)
    {
        var tracker = new ResponseDisciplineTrackingClient(orchestration);
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
    /// Creates an OrchestrationClient configured from the standard environment variables,
    /// preferring a per-message <c>message.metadata.callbackToken</c> from the inbound message body when present.
    /// </summary>
    public static IOrchestrationClient FromEnvironment(string? inboundMessageBody)
    {
        var callbackUrl = ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackUrlEnvVar);
        var callbackToken = TryReadCallbackToken(inboundMessageBody) ?? ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackTokenEnvVar);

        return new OrchestrationClient(callbackUrl, callbackToken);
    }

    /// <summary>
    /// Creates an OrchestrationClient configured from the standard environment variables,
    /// preferring a per-message <c>message.metadata.callbackToken</c> from the inbound message body when present.
    /// </summary>
    public static IOrchestrationClient FromEnvironment(JsonElement inboundMessageBody)
    {
        var callbackUrl = ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackUrlEnvVar);
        var callbackToken = TryReadCallbackToken(inboundMessageBody) ?? ReadRequiredEnvironmentVariable(
            AgentSdkEnvironmentContract.CallbackTokenEnvVar);

        return new OrchestrationClient(callbackUrl, callbackToken);
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

    private static string? TryReadCallbackToken(string? inboundMessageBody)
    {
        if (string.IsNullOrWhiteSpace(inboundMessageBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inboundMessageBody);
            return TryReadCallbackToken(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadCallbackToken(JsonElement inboundMessageBody)
    {
        if (inboundMessageBody.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var payload = inboundMessageBody;
        if (inboundMessageBody.TryGetProperty("params", out var parameters))
        {
            payload = parameters;
        }

        if (!payload.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty(CallbackTokenPayloadField, out var metadataToken) ||
            metadataToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var token = metadataToken.GetString();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
