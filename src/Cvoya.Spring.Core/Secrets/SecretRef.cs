// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// An immutable, structural identifier for a secret. The triple
/// (<see cref="Scope"/>, <see cref="OwnerId"/>, <see cref="Name"/>)
/// is unique within a tenant. Tenant isolation is enforced by
/// <see cref="ISecretRegistry"/>, which reads the current tenant
/// from <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/>.
/// </summary>
/// <param name="Scope">The ownership scope.</param>
/// <param name="OwnerId">
/// The scope-specific owner identifier. For <see cref="SecretScope.Unit"/>
/// this is the unit's Guid; for <see cref="SecretScope.Tenant"/> the tenant
/// Guid (matches the ambient tenant); for <see cref="SecretScope.Platform"/>
/// the value is <c>null</c>.
/// </param>
/// <param name="Name">
/// The secret name. Case-sensitive; the registry enforces a unique index
/// on (TenantId, Scope, OwnerId, Name).
/// </param>
public record SecretRef(SecretScope Scope, Guid? OwnerId, string Name);
