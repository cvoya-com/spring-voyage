// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Returns the tenant identifier for the current execution context.
/// The OSS implementation is a singleton that resolves to
/// <see cref="OssTenantIds.Default"/>; the private cloud repo swaps in a
/// scoped implementation that resolves the tenant from the request
/// principal.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Gets the tenant identifier for the current execution context.
    /// Never <see cref="Guid.Empty"/>.
    /// </summary>
    Guid CurrentTenantId { get; }
}
