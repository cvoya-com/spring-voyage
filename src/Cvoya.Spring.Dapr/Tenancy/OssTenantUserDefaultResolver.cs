// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// OSS-default <see cref="ITenantUserDefaultResolver"/>. The OSS deployment
/// ships with exactly one <c>TenantUser</c> — the operator pinned at
/// <see cref="OssTenantUserIds.Operator"/> — and per ADR-0062 § 1 every
/// Human-insert path that does not carry an explicit binding stamps that
/// principal on the new row. The resolver returns the literal constant
/// unconditionally; it does not consult tenant context, auth state, or
/// configuration because the OSS rule is the same regardless.
/// </summary>
/// <remarks>
/// The cloud overlay replaces this implementation via <c>TryAdd*</c> in
/// DI; the replacement resolves the current authenticated principal and
/// returns its <c>TenantUser</c> id. Both implementations satisfy the
/// "never <see cref="System.Guid.Empty"/>" contract.
/// </remarks>
public sealed class OssTenantUserDefaultResolver : ITenantUserDefaultResolver
{
    /// <inheritdoc />
    public Task<Guid> ResolveDefaultAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(OssTenantUserIds.Operator);
}
