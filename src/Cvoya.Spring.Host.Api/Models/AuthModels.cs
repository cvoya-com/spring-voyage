// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json.Serialization;

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
/// <param name="Id">The user's unique identifier.</param>
/// <param name="GitHubLogin">The user's GitHub username.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="Email">The user's email address, if available.</param>
/// <param name="AvatarUrl">The URL to the user's avatar image.</param>
public record UserProfileResponse(
    Guid Id,
    string GitHubLogin,
    string DisplayName,
    string? Email,
    string? AvatarUrl);

/// <summary>
/// Response body from GitHub's OAuth access token endpoint.
/// </summary>
internal record GitHubTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;
}

/// <summary>
/// Response body from GitHub's user profile API endpoint.
/// </summary>
internal record GitHubUserResponse
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("login")]
    public string Login { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }
}
