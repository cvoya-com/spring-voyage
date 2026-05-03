// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists the structural metadata for a secret version (scope + owner +
/// name + <see cref="Version"/>) and a pointer (<see cref="StoreKey"/>)
/// to the backing store entry that holds the plaintext. The plaintext
/// itself is never stored on this entity.
///
/// <para>
/// <b>Scope/owner split.</b> <see cref="OwnerId"/> is a nullable Guid:
/// for <see cref="SecretScope.Unit"/> it is the unit's Guid; for
/// <see cref="SecretScope.Tenant"/> it is the tenant Guid (matches
/// <see cref="TenantId"/>); for <see cref="SecretScope.Platform"/> it is
/// <c>null</c>.
/// </para>
///
/// <para>
/// <b>Multi-version coexistence.</b> A single secret named
/// <c>(scope, owner, name)</c> is persisted as N rows — one per
/// retained version. Each rotation inserts a new row at
/// <c>max(Version)+1</c>; the prior rows stay until pruned. The
/// resolver selects the MAX(Version) row by default, or a pinned
/// version when the caller supplies one.
/// </para>
/// </summary>
public class SecretRegistryEntry : ITenantScopedEntity
{
    /// <summary>Unique identifier for the registry entry.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns the entry.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Ownership scope (unit / tenant / platform).</summary>
    public SecretScope Scope { get; set; }

    /// <summary>
    /// Scope-specific owner Guid. Unit Guid for
    /// <see cref="SecretScope.Unit"/>; tenant Guid (matches
    /// <see cref="TenantId"/>) for <see cref="SecretScope.Tenant"/>;
    /// <c>null</c> for <see cref="SecretScope.Platform"/>.
    /// </summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Secret name (case-sensitive within the triple).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Opaque pointer into the backing <see cref="ISecretStore"/>. Does
    /// not encode tenant or structural information — the registry row
    /// owns the structure.
    /// </summary>
    public string StoreKey { get; set; } = string.Empty;

    /// <summary>
    /// Who owns the storage slot that <see cref="StoreKey"/> points at.
    /// </summary>
    public SecretOrigin Origin { get; set; }

    /// <summary>
    /// Monotonically-increasing version number, bumped by rotations.
    /// </summary>
    public int? Version { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last-update timestamp (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
