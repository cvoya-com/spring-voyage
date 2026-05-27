// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Resolves the <c>TenantUser</c> id that should be stamped on a new
/// <c>HumanEntity</c> row when no explicit binding is supplied at the call
/// site (ADR-0062 § 1). Every Human-insert path — package install, portal
/// member-add, CLI member-add, test seeders, the auth-time
/// <c>HumanIdentityResolver</c> upsert — calls this resolver so the NOT NULL
/// <c>humans.tenant_user_id</c> FK is always populated without each call
/// site re-implementing the OSS-default-vs-cloud rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>OSS implementation</b>: always returns
/// <see cref="OssTenantUserIds.Operator"/>. The OSS deployment ships with
/// exactly one <c>TenantUser</c> — the operator running the install — and
/// every Human in the tenant binds to that one principal by definition.
/// </para>
/// <para>
/// <b>Cloud overlay</b>: returns the calling <c>TenantUser</c> from the
/// authenticated caller. Explicit overrides (CLI <c>--as &lt;tenant-user-ref&gt;</c>,
/// install-flow flags) take precedence at the call site, not in the
/// resolver — the resolver answers the "no explicit binding supplied"
/// case only.
/// </para>
/// <para>
/// Mirrors the shape of <see cref="ITenantContext"/>: one resolver, called
/// once at the right moment, deployment-overrideable via DI. Lives in
/// <see cref="Cvoya.Spring.Core.Tenancy"/> so the cloud overlay can register
/// its replacement without taking a dependency on
/// <c>Cvoya.Spring.Dapr</c>.
/// </para>
/// </remarks>
public interface ITenantUserDefaultResolver
{
    /// <summary>
    /// Returns the <c>TenantUser</c> id to stamp on a new <c>HumanEntity</c>
    /// row when the call site did not supply an explicit binding. Never
    /// <see cref="System.Guid.Empty"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<Guid> ResolveDefaultAsync(CancellationToken cancellationToken = default);
}
