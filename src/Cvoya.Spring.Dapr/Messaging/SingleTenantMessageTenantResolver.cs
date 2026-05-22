// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// OSS-default <see cref="IMessageTenantResolver"/>. Every address
/// resolves to <see cref="OssTenantIds.Default"/> because the OSS platform
/// ships functionally single-tenant (see
/// <see cref="OssTenantIds"/> for the rationale on the pinned id).
/// </summary>
/// <remarks>
/// The cross-tenant containment gate from ADR-0039 §3 is therefore
/// structurally impossible to violate on an OSS install — every caller
/// and every target evaluate to the same tenant id, so the comparison in
/// <see cref="MessageDeliveryService"/> always succeeds. The cloud
/// overlay registers a tenant-aware resolver via the standard
/// <c>TryAddSingleton</c> seam and the handler picks it up without code
/// changes.
/// </remarks>
public sealed class SingleTenantMessageTenantResolver : IMessageTenantResolver
{
    /// <inheritdoc />
    public Task<Guid> GetTenantForAddressAsync(Address address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Task.FromResult(OssTenantIds.Default);
    }
}
