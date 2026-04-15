// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

/// <summary>
/// Discriminated-style result for a <c>POST /login/oauth/access_token</c>
/// call. Callers check <see cref="Error"/> for the failure path; when it is
/// <c>null</c> the remaining fields are populated.
/// </summary>
/// <param name="AccessToken">The user-to-server access token, or <c>null</c> on error.</param>
/// <param name="RefreshToken">The refresh token, if GitHub issued one.</param>
/// <param name="ExpiresAt">When the access token expires, or <c>null</c> when unspecified.</param>
/// <param name="GrantedScopes">Space-joined scopes GitHub actually granted.</param>
/// <param name="Error">GitHub error code from the response, if the exchange failed.</param>
/// <param name="ErrorDescription">GitHub human-readable error description, if any.</param>
public record OAuthTokenExchangeResult(
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string GrantedScopes,
    string? Error,
    string? ErrorDescription);