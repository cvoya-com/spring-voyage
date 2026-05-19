// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Outcome of processing an inbound GitHub webhook.
/// Discriminated via <see cref="Outcome"/> so callers can distinguish
/// authentication failure (HTTP 401) from an accepted-but-ignored event (HTTP 202)
/// from one-or-more translated domain messages that must still be routed.
///
/// <para>
/// ADR-0047 §10 enables within-tenant fan-out: a single inbound webhook
/// payload for a repo can match every unit binding in the receiving tenant
/// whose <c>(owner, repo)</c> equals the payload's coordinates. Each
/// binding's per-binding filter (<see cref="UnitGitHubConfig.IncludeLabels"/>
/// and siblings) decides whether that unit processes the event. The
/// connector surfaces the filter-passing messages as a list; the endpoint
/// routes each in turn.
/// </para>
/// </summary>
/// <param name="Outcome">Which of the three outcomes occurred.</param>
/// <param name="Messages">
/// The translated domain messages, one per binding that matched the
/// inbound payload AND passed its own filter, when <see cref="Outcome"/>
/// is <see cref="WebhookOutcome.Translated"/>; otherwise empty.
/// </param>
public record WebhookHandleResult(WebhookOutcome Outcome, IReadOnlyList<Message> Messages)
{
    /// <summary>
    /// The webhook signature did not match the configured secret.
    /// </summary>
    public static WebhookHandleResult InvalidSignature { get; } =
        new(WebhookOutcome.InvalidSignature, Array.Empty<Message>());

    /// <summary>
    /// The signature was valid but the event type (or event action) is not
    /// one the connector translates into a domain message — or every
    /// matching binding's filter dropped the event. Callers should
    /// acknowledge with 202 so GitHub does not retry.
    /// </summary>
    public static WebhookHandleResult Ignored { get; } =
        new(WebhookOutcome.Ignored, Array.Empty<Message>());

    /// <summary>
    /// Produces a result indicating the event was translated and at least
    /// one binding's filter passed it.
    /// </summary>
    /// <param name="messages">The translated domain messages, one per filter-passing binding.</param>
    public static WebhookHandleResult Translated(IReadOnlyList<Message> messages) =>
        messages.Count == 0
            ? Ignored
            : new(WebhookOutcome.Translated, messages);
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
