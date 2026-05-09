// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Outcome of processing an inbound GitHub webhook.
/// Discriminated via <see cref="Outcome"/> so callers can distinguish
/// authentication failure (HTTP 401) from an accepted-but-ignored event (HTTP 202)
/// from a translated domain message that must still be routed.
/// </summary>
/// <param name="Outcome">Which of the three outcomes occurred.</param>
/// <param name="Message">The translated domain message when <see cref="Outcome"/> is <see cref="WebhookOutcome.Translated"/>; otherwise <c>null</c>.</param>
public record WebhookHandleResult(WebhookOutcome Outcome, Message? Message)
{
    /// <summary>
    /// The webhook signature did not match the configured secret.
    /// </summary>
    public static WebhookHandleResult InvalidSignature { get; } =
        new(WebhookOutcome.InvalidSignature, null);

    /// <summary>
    /// The signature was valid but the event type (or event action) is not one
    /// the connector translates into a domain message. Callers should acknowledge
    /// with 202 so GitHub does not retry.
    /// </summary>
    public static WebhookHandleResult Ignored { get; } =
        new(WebhookOutcome.Ignored, null);

    /// <summary>
    /// Produces a result indicating the event was translated into a domain message
    /// that still needs to be routed.
    /// </summary>
    /// <param name="message">The translated domain message.</param>
    public static WebhookHandleResult Translated(Message message) =>
        new(WebhookOutcome.Translated, message);
}

/// <summary>
/// The three possible outcomes when processing a GitHub webhook.
/// </summary>
public enum WebhookOutcome
{
    /// <summary>The HMAC signature was missing or did not match — authentication failure.</summary>
    InvalidSignature = 0,

    /// <summary>The signature was valid but the event is not one the connector handles.</summary>
    Ignored = 1,

    /// <summary>The signature was valid and the event produced a domain message that must be routed.</summary>
    Translated = 2,
}
