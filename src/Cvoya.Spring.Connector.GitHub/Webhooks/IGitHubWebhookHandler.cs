// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Webhooks;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Translates inbound GitHub webhook payloads into domain <see cref="Message"/> objects.
/// Extracted so callers and tests can substitute a mock translator without touching Octokit.
/// </summary>
public interface IGitHubWebhookHandler
{
    /// <summary>
    /// Translates a GitHub webhook event into a domain message.
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The parsed JSON payload.</param>
    /// <returns>A domain <see cref="Message"/>, or <c>null</c> if the event type is not handled.</returns>
    Message? TranslateEvent(string eventType, JsonElement payload);

    /// <summary>
    /// Derives the set of cache tags that the connector should invalidate in
    /// response to <paramref name="eventType"/> + <paramref name="payload"/>.
    /// Returns an empty sequence when the event has no cacheable surface (or
    /// the payload is missing the required identifiers).
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The parsed JSON payload.</param>
    IReadOnlyList<string> DeriveInvalidationTags(string eventType, JsonElement payload);
}