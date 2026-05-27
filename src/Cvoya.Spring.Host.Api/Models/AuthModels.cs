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
/// <param name="UserId">The username / login subject claim. Used for display only.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="Id">
/// The authenticated human's stable Guid identity (the actor's id). This is
/// the canonical primitive callers use to answer "is this me?" — see
/// <see cref="ParticipantRef.Id"/>. Identity equality is a typed-Guid
/// operation; the textual <see cref="Address"/> below is for display /
/// routing only and may legitimately appear in more than one shape across
/// the wire (#2082).
/// </param>
/// <param name="Address">
/// The canonical wire-form <c>human:&lt;32-hex&gt;</c> address for the
/// authenticated user. Use this for display, routing, and as the
/// <c>from</c> field on <c>POST /messages</c>. <b>Do not</b> use it for
/// identity comparisons — compare on <see cref="Id"/> instead.
/// </param>
public record UserProfileResponse(
    string UserId,
    string DisplayName,
    Guid Id,
    string? Address = null,
    Guid? TenantUserId = null);
