// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Server-side session metadata for an OAuth-authorized user. The actual
/// tokens are kept opaque in <see cref="ISecretStore"/>; this record stores
/// the access- and refresh-token store keys together with the user-visible
/// metadata needed by session lookups.
/// </summary>
/// <param name="SessionId">
/// Server-issued opaque identifier (random, non-guessable). The only value
/// the caller ever sees — tokens are NEVER returned to the caller.
/// </param>
/// <param name="Login">The GitHub login of the authorized user.</param>
/// <param name="UserId">The GitHub numeric user id.</param>
/// <param name="Scopes">Space-joined OAuth scopes GitHub actually granted.</param>
/// <param name="AccessTokenStoreKey">
/// The opaque <see cref="ISecretStore"/> key holding the access-token
/// plaintext. Never exposed to the caller.
/// </param>
/// <param name="RefreshTokenStoreKey">
/// Optional <see cref="ISecretStore"/> key holding the refresh-token
/// plaintext when GitHub issued one. OAuth Apps without a user-to-server
/// expiring token (the classic long-lived case) return <c>null</c> here.
/// </param>
/// <param name="ExpiresAt">
/// When GitHub says the access token expires, or <c>null</c> when the token
/// has no advertised expiry.
/// </param>
/// <param name="CreatedAt">When the session record was created.</param>
/// <param name="ClientState">
/// Opaque client state echoed back from the authorize request, if any.
/// </param>
/// <param name="Initiation">
/// Typed payload describing what initiated the flow per ADR-0047 §13.
/// <c>null</c> when the caller did not declare an intent (legacy
/// behaviour). Surfaced on the session so the portal / CLI can drive a
/// post-callback wizard / refresh without re-deriving the intent.
/// </param>
/// <param name="PatSecretName">
/// Tenant-scoped secret name the OAuth-issued PAT was persisted under
/// per ADR-0047 §5. Set when <see cref="Initiation"/> indicates a flow
/// that ought to mint a binding-usable credential
/// (<see cref="OAuthInitiationIntent.UserIdentitySurface"/> or
/// <see cref="OAuthInitiationIntent.BindingWizard"/>); <c>null</c> for
/// the legacy unspecified path that only powers
/// <c>list-repositories</c> and does not need a binding-side credential.
/// The wizard reads this off the post-callback session to pre-fill
/// <c>pat_secret_name</c> on the binding-create call.
/// </param>
public record OAuthSession(
    string SessionId,
    string Login,
    long UserId,
    string Scopes,
    string AccessTokenStoreKey,
    string? RefreshTokenStoreKey,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string? ClientState,
    OAuthInitiationContext? Initiation = null,
    string? PatSecretName = null);
