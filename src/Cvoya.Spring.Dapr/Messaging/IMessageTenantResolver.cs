// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves the tenant that owns a given <see cref="Address"/> for the
/// purposes of ADR-0039 §3 gate 6 (cross-tenant containment).
/// </summary>
/// <remarks>
/// <para>
/// Every messaging tool handler invocation enforces gate 6 — the
/// caller's resolved tenant and (when a target is supplied) the target's
/// resolved tenant must match the <c>tenantId</c> claim carried on the
/// validated callback token. The resolver is the seam the handler uses to
/// translate addresses into tenant ownership.
/// </para>
/// <para>
/// The OSS overlay ships single-tenant: every address resolves to
/// <see cref="Core.Tenancy.OssTenantIds.Default"/> and the gate is a
/// structural impossibility to violate (the per-tenant signing key
/// already partitions valid tokens by tenant). The cloud overlay swaps
/// in a tenant-aware implementation that consults the persisted
/// <c>UnitDefinitions</c> / <c>AgentDefinitions</c> rows so a forged or
/// replayed token whose claim points at a foreign-tenant entity is
/// rejected at the handler instead of dispatched.
/// </para>
/// <para>
/// Implementations are registered through the standard
/// <c>TryAddSingleton</c> seam (see
/// <c>ServiceCollectionExtensions.Messaging</c>). The interface is
/// intentionally async-Task-of-Guid so cloud implementations can issue
/// directory or database lookups without changing the handler signatures.
/// </para>
/// </remarks>
public interface IMessageTenantResolver
{
    /// <summary>
    /// Returns the tenant that owns <paramref name="address"/>. Implementations
    /// must return a valid tenant id for any address that resolves to a
    /// platform-managed entity, or throw if the address cannot be resolved
    /// (the handler treats throws as a hard failure and surfaces them to
    /// the caller).
    /// </summary>
    /// <param name="address">The address to resolve.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The tenant id that owns the address.</returns>
    Task<Guid> GetTenantForAddressAsync(Address address, CancellationToken cancellationToken = default);
}
