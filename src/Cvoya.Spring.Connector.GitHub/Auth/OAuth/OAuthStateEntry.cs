// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// A pending authorization record stored by <see cref="IOAuthStateStore"/>
/// between issuing the authorize URL and receiving the callback. Captures
/// just enough to CSRF-validate the callback and reconstruct the scopes the
/// caller originally asked for — no secrets live here.
/// </summary>
/// <param name="State">
/// The random <c>state</c> parameter echoed back by GitHub. Treated as a
/// one-time-use token: consumed by the callback and then deleted.
/// </param>
/// <param name="Scopes">
/// The space-joined OAuth scopes the caller requested. Preserved so the
/// callback can round-trip them into the session metadata without a second
/// API call.
/// </param>
/// <param name="RedirectUri">
/// The redirect URI the authorize URL declared. GitHub validates this at
/// the token-exchange step, so we echo exactly what we originally sent to
/// keep the contract tight.
/// </param>
/// <param name="ExpiresAt">
/// The UTC instant after which the entry is considered expired. The store
/// refuses to return expired entries — the callback surfaces a 400.
/// </param>
/// <param name="ClientState">
/// Opaque state payload the caller can attach at authorize time and read
/// back from the session after callback. Useful for the portal's "resume
/// where the user left off" flow. Not interpreted by the connector.
/// </param>
public record OAuthStateEntry(
    string State,
    string Scopes,
    string RedirectUri,
    DateTimeOffset ExpiresAt,
    string? ClientState);