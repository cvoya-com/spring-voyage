// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

/// <summary>
/// Represents a user authenticated via GitHub OAuth.
/// Stores the GitHub identity and profile information for session management.
/// </summary>
public class UserEntity
{
    /// <summary>Gets or sets the unique identifier for the user.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the GitHub user ID (numeric, stored as string).</summary>
    public string GitHubId { get; set; } = string.Empty;

    /// <summary>Gets or sets the GitHub login (username).</summary>
    public string GitHubLogin { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's email address, if available.</summary>
    public string? Email { get; set; }

    /// <summary>Gets or sets the URL to the user's GitHub avatar image.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Gets or sets the timestamp when the user was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the user was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
