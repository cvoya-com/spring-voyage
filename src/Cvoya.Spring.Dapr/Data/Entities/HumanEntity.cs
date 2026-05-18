// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Persisted record for a human user in the Spring Voyage platform.
/// Provides a stable UUID that decouples identity from the JWT
/// username claim so actor keys, permission maps, and inbox rows
/// survive a username rename without re-keying.
/// </summary>
public class HumanEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the stable UUID primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this human record.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Gets or sets the JWT subject claim (NameIdentifier). Unique within
    /// a tenant. Used to look up the UUID at every authenticated boundary.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable display name. Defaults to the
    /// username when not explicitly set.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional single-line description (ADR-0046 §7).
    /// Editable post-install via the Human × Config tab and seeded by the
    /// package author via the <c>description:</c> field on a <c>- human:</c>
    /// member entry or its <c>HumanTemplate</c>. Null when no description
    /// has been set; the storage column is nullable.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the optional e-mail address for this human.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// Gets or sets the global permission level for this human. Defaults to
    /// <see cref="PermissionLevel.Operator"/> per the OSS unblock decision
    /// captured on <see cref="HumanEntity"/> (#1473 / #1479) — the long-term
    /// shape moves to owner-by-creation + thread membership.
    /// </summary>
    public PermissionLevel PermissionLevel { get; set; } = PermissionLevel.Operator;

    /// <summary>
    /// Gets or sets the per-human notification preferences. Stored as a
    /// <c>jsonb</c> column; <c>null</c> means "no explicit preferences set"
    /// and downstream routing applies the platform default.
    /// </summary>
    public NotificationPreferences? NotificationPreferences { get; set; }

    /// <summary>Gets or sets the timestamp when the record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
