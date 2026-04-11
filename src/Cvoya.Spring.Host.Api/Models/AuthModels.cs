// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Request body for creating a new API token.
/// </summary>
/// <param name="Name">A human-readable name for the token.</param>
/// <param name="Scopes">Optional list of scopes to restrict token access.</param>
/// <param name="ExpiresAt">Optional expiration timestamp. Null means no expiration.</param>
public record CreateTokenRequest(
    string Name,
    IReadOnlyList<string>? Scopes = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Response body representing an API token's metadata (never includes the raw token value).
/// </summary>
/// <param name="Name">The display name of the token.</param>
/// <param name="CreatedAt">When the token was created.</param>
/// <param name="ExpiresAt">When the token expires, or null if it does not expire.</param>
/// <param name="Scopes">The scopes granted to this token.</param>
public record TokenResponse(
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string>? Scopes);

/// <summary>
/// Response body returned when a token is first created. Contains the raw token value
/// which is only shown once and never stored.
/// </summary>
/// <param name="Token">The raw token value. Store this securely; it cannot be retrieved again.</param>
/// <param name="Name">The display name of the token.</param>
public record CreateTokenResponse(
    string Token,
    string Name);

/// <summary>
/// Response body returned from the /me endpoint with the current user's profile.
/// </summary>
/// <param name="UserId">The user's identifier.</param>
/// <param name="DisplayName">The user's display name.</param>
public record UserProfileResponse(
    string UserId,
    string DisplayName);