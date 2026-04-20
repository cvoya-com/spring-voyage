// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Returns the tenant identifier for the current execution context.
/// The OSS implementation is a singleton that reads a configured
/// default tenant id (<c>Secrets:DefaultTenantId</c>, defaulting to
/// <c>"default"</c>). The private cloud repo swaps in a scoped
/// implementation that resolves the tenant from the request principal.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Gets the tenant identifier for the current execution context.
    /// Never null or empty.
    /// </summary>
    string CurrentTenantId { get; }
}