// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Minimal <see cref="ITenantContext"/> that returns a caller-supplied
/// tenant id. Intended for test harnesses and the design-time /
/// back-compat <see cref="Cvoya.Spring.Dapr.Data.SpringDbContext"/>
/// constructor that does not resolve <see cref="ITenantContext"/> via
/// DI. Not registered in <c>AddCvoyaSpringDapr</c> — runtime code uses
/// <see cref="ConfiguredTenantContext"/> (or a private-cloud override).
/// </summary>
public sealed class StaticTenantContext : ITenantContext
{
    /// <summary>
    /// Creates a new <see cref="StaticTenantContext"/>.
    /// </summary>
    /// <param name="tenantId">Tenant id returned by
    /// <see cref="CurrentTenantId"/>. Must not be null or empty.</param>
    public StaticTenantContext(string tenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        CurrentTenantId = tenantId;
    }

    /// <inheritdoc />
    public string CurrentTenantId { get; }
}