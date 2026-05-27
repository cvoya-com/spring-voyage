// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

/// <summary>
/// One persisted in-flight Slack OAuth authorization state. Consumed
/// exactly once by the callback handler; expired entries are dropped
/// by the store on next access.
/// </summary>
/// <param name="State">The opaque cryptographic state token.</param>
/// <param name="Scopes">Space-joined OAuth scope list issued with the authorize URL.</param>
/// <param name="RedirectUri">The redirect_uri pinned at authorize time.</param>
/// <param name="ExpiresAt">UTC instant after which the state is considered expired.</param>
/// <param name="ClientState">Opaque payload echoed back to the callback (e.g. portal targetOrigin).</param>
public record SlackOAuthStateEntry(
    string State,
    string Scopes,
    string RedirectUri,
    DateTimeOffset ExpiresAt,
    string? ClientState);

/// <summary>
/// Store for in-flight Slack OAuth state entries. Default OSS impl
/// is in-memory; cloud overlays can substitute a Redis-backed
/// equivalent via DI.
/// </summary>
public interface ISlackOAuthStateStore
{
    /// <summary>Persists <paramref name="entry"/>.</summary>
    Task SaveAsync(SlackOAuthStateEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically reads and removes the entry for
    /// <paramref name="state"/>. Returns <c>null</c> when no entry
    /// exists or the entry has expired.
    /// </summary>
    Task<SlackOAuthStateEntry?> ConsumeAsync(string state, CancellationToken cancellationToken);
}
