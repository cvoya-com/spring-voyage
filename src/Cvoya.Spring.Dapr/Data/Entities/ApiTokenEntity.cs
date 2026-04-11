// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

/// <summary>
/// Represents an API token used for authenticating requests to the platform.
/// </summary>
public class ApiTokenEntity
{
    /// <summary>Gets or sets the unique identifier for the API token.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the identifier of the user associated with this token.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the hash of the token value. The raw token is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name of the token.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the comma-separated list of scopes granted to this token.</summary>
    public string? Scopes { get; set; }

    /// <summary>Gets or sets the expiration timestamp, or null if the token does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Gets or sets the timestamp when the token was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the token was revoked, or null if active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

}