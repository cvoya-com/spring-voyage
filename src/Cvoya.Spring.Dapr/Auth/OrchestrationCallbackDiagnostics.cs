// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Surfaces orchestration callback-token rejections as
/// operator-visible signals (issue #2582).
/// </summary>
/// <remarks>
/// <para>
/// A callback-token rejection (<c>/v1/runtime/orchestration</c> → 401) is a
/// whole failure class — expired / malformed / wrong-tenant tokens that
/// cause a persistent agent to lose <c>sv.messaging.send</c> /
/// <c>sv.messaging.broadcast</c> (see #2580). Before this helper the
/// rejection was effectively invisible:
/// no activity, and only the ambient request log at <c>info</c>. This helper
/// emits a single <c>warning</c> log line carrying the structured
/// <see cref="CallbackTokenValidationReason"/>, plus one
/// <see cref="ActivityEventType.ErrorOccurred"/> activity so the failure
/// flows onto the activity stream / OTLP feed (#2492).
/// </para>
/// </remarks>
public sealed class OrchestrationCallbackDiagnostics(
    IActivityEventBus activityEventBus,
    ILogger<OrchestrationCallbackDiagnostics> logger)
{
    private readonly IActivityEventBus _activityEventBus = activityEventBus;
    private readonly ILogger<OrchestrationCallbackDiagnostics> _logger = logger;

    /// <summary>
    /// Records a callback-token rejection: a <c>warning</c> log line and a
    /// matching <see cref="ActivityEventType.ErrorOccurred"/> activity.
    /// Best-effort — a failure to publish the activity never propagates.
    /// </summary>
    /// <param name="exception">The validation failure raised by the validator.</param>
    /// <param name="rejectedToken">
    /// The raw bearer token that was rejected, when one was present. Used to
    /// recover a best-effort subject address for the activity even though
    /// the token did not validate — a rejected token's <c>sv_addr</c> claim
    /// is unverified but is still the most precise subject available.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task RecordRejectionAsync(
        CallbackTokenValidationException exception,
        string? rejectedToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var subject = TryResolveSubject(rejectedToken);

        // #2582: warning, not info — a rejected callback token is an
        // operator-actionable condition, and the structured reason is the
        // signal an operator greps for.
        _logger.LogWarning(
            "Orchestration callback token rejected ({Reason}) for subject {Subject}: {Message}",
            exception.Reason,
            subject,
            exception.Message);

        var details = JsonSerializer.SerializeToElement(new
        {
            reason = exception.Reason.ToString(),
            message = exception.Message,
            subject = subject.ToString(),
        });

        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            subject,
            ActivityEventType.ErrorOccurred,
            ActivitySeverity.Warning,
            $"Orchestration callback token rejected: {exception.Reason}.",
            details);

        try
        {
            await _activityEventBus.PublishAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: the warning log above is the durable signal; a
            // bus failure must not turn a 401 into a 500.
            _logger.LogWarning(
                ex,
                "Failed to emit ErrorOccurred activity for a rejected orchestration callback token.");
        }
    }

    /// <summary>
    /// Recovers a best-effort subject <see cref="Address"/> from the rejected
    /// token's unverified <c>sv_addr</c> claim. The token did not validate,
    /// so the claim is untrusted — but for a diagnostic activity it is the
    /// most precise subject available, and falls back to a synthetic
    /// <c>unit://</c> sentinel when the token carries nothing usable.
    /// </summary>
    private static Address TryResolveSubject(string? rejectedToken)
    {
        if (!string.IsNullOrWhiteSpace(rejectedToken))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                if (handler.CanReadToken(rejectedToken))
                {
                    var unverified = handler.ReadJwtToken(rejectedToken);
                    var addressClaim = unverified.Claims
                        .FirstOrDefault(c => c.Type == CallbackTokenClaimNames.AgentAddress)?.Value;
                    if (Address.TryParse(addressClaim, out var parsed) && parsed is not null)
                    {
                        return parsed;
                    }
                }
            }
            catch
            {
                // Malformed token — fall through to the sentinel subject.
            }
        }

        // No usable subject on the token. Use a stable, all-zero unit
        // sentinel so the activity still has a well-formed Source.
        return new Address(Address.UnitScheme, Guid.Empty);
    }
}
