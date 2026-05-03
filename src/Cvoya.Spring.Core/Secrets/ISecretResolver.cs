// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Server-side read surface for secrets. <see cref="ResolveAsync"/> is
/// the ONLY entry point through which a plaintext value may leave the
/// system: the HTTP API never returns plaintext, and every agent or
/// connector that needs a secret at runtime goes through this interface.
///
/// Implementations compose <see cref="ISecretRegistry"/> (for the
/// structural lookup) with <see cref="ISecretStore"/> (for the value
/// fetch). The private cloud repo layers audit-log decoration and RBAC
/// checks onto this interface via DI without touching call sites.
///
/// <para>
/// <b>Version pinning (wave 7 A5).</b> The interface offers two shapes:
/// the historical "latest only" overloads and a
/// <paramref name="version"/>-aware overload that lets callers pin to
/// a specific prior version. <c>version = null</c> means latest
/// (default resolve behavior); an integer value pins to that specific
/// version and returns <see cref="SecretResolvePath.NotFound"/> when
/// no matching version exists — the resolver never silently returns a
/// different version to a pinned caller.
/// </para>
///
/// <para>
/// <b>DI decoration pattern.</b> <see cref="ISecretResolver"/> is the
/// primary extension point for audit logging, RBAC checks, metrics, and
/// redaction. The OSS default (<c>ComposedSecretResolver</c>) is
/// registered with <c>TryAddScoped</c>, so consumers can wrap it at the
/// DI layer AFTER calling <c>AddCvoyaSpringDapr</c>. There is no
/// Scrutor dependency in the core — the pattern is manual but stable.
/// </para>
///
/// <example>
/// <code>
/// services.AddCvoyaSpringDapr(configuration);
///
/// // Wrap the concrete resolver. The 'inner' lookup uses the concrete
/// // type so the decorator forwards to the built-in ComposedSecretResolver
/// // (or whatever was registered for ISecretResolver before this call).
/// services.AddScoped&lt;ComposedSecretResolver&gt;();
/// services.Replace(ServiceDescriptor.Scoped&lt;ISecretResolver&gt;(sp =&gt;
///     new AuditingSecretResolver(
///         inner: sp.GetRequiredService&lt;ComposedSecretResolver&gt;(),
///         auditLog: sp.GetRequiredService&lt;IAuditLog&gt;())));
///
/// // Or the idempotent shape — this ONLY wraps if the current
/// // ISecretResolver registration is not already the decorator, so a
/// // second AddCvoyaSpringDapr() call from a test harness does not
/// // double-wrap. See docs/developer/secret-audit.md for the pattern.
/// </code>
/// </example>
///
/// <para>
/// Re-registering the OSS default is idempotent. <c>AddCvoyaSpringDapr</c>
/// uses <c>TryAddScoped</c> for <see cref="ISecretResolver"/>, so a
/// consumer that has already layered a decorator will NOT be overwritten
/// by a subsequent call to <c>AddCvoyaSpringDapr</c> — the decorator is
/// preserved, and the second call becomes a no-op at this registration.
/// </para>
///
/// <para>
/// <b>Invariants an audit decorator can rely on.</b> On every call the
/// decorator sees:
/// <list type="bullet">
///   <item><description>The requested <see cref="SecretRef"/> (scope, owner, name) and the version pin (or <c>null</c> for latest).</description></item>
///   <item><description>The <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> in effect — the decorator can resolve this from DI, OSS filters every registry query by the current tenant regardless.</description></item>
///   <item><description>The <see cref="SecretResolution"/> returned by the inner resolver, including <see cref="SecretResolution.Path"/> (direct / inherited / not-found), <see cref="SecretResolution.EffectiveRef"/>, and <see cref="SecretResolution.Version"/>.</description></item>
/// </list>
/// Decorators MUST NOT mutate the inner resolver's return value and
/// MUST NOT log plaintext — the <see cref="SecretResolution.Value"/>
/// is the one field that never belongs in an audit record. See
/// <c>docs/developer/secret-audit.md</c> for the full best-practice
/// list.
/// </para>
/// </summary>
public interface ISecretResolver
{
    /// <summary>
    /// Resolves the latest-version plaintext value for the given
    /// structural reference in the current tenant, or <c>null</c> if no
    /// such reference exists (or the value is missing from the
    /// underlying store). Convenience wrapper over
    /// <see cref="ResolveWithPathAsync(SecretRef, CancellationToken)"/>.
    ///
    /// <para>
    /// For <see cref="SecretScope.Unit"/> requests this method applies
    /// the Unit → Tenant inheritance fall-through: if the unit entry is
    /// absent, the resolver transparently falls back to the same-name
    /// tenant entry, subject to the access-policy check at BOTH levels.
    /// The fall-through can be disabled via configuration — see
    /// <c>Secrets:InheritTenantFromUnit</c>.
    /// </para>
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plaintext value, or <c>null</c>.</returns>
    Task<string?> ResolveAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Resolves the latest-version plaintext value for the given
    /// structural reference and returns the <see cref="SecretResolution"/>
    /// describing the resolve path — direct hit, inherited from tenant,
    /// or not found. Audit-log / metrics decorators consume this
    /// overload so they can record the path taken.
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SecretResolution"/> whose <c>Path</c> is
    /// <see cref="SecretResolvePath.Direct"/>,
    /// <see cref="SecretResolvePath.InheritedFromTenant"/>, or
    /// <see cref="SecretResolvePath.NotFound"/>.
    /// </returns>
    Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Resolves the plaintext value for the given structural reference,
    /// optionally pinned to a specific <paramref name="version"/>
    /// (<c>null</c> means latest). Returns the
    /// <see cref="SecretResolution"/> describing which registry entry
    /// produced the value.
    ///
    /// <para>
    /// <b>Pinned reads never silently return a different version.</b>
    /// If the requested version does not exist at the requested scope,
    /// the resolver consults the Unit → Tenant inheritance fall-through
    /// (when enabled and the requested scope is
    /// <see cref="SecretScope.Unit"/>): the same
    /// <paramref name="version"/> is looked up at the tenant scope. If
    /// neither scope has that version, the result is
    /// <see cref="SecretResolvePath.NotFound"/>.
    /// </para>
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="version">
    /// The version to pin to, or <c>null</c> for the latest version.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, int? version, CancellationToken ct);

    /// <summary>
    /// Lists the structural references visible to the current tenant for
    /// the given scope and owner. This is a thin wrapper over
    /// <see cref="ISecretRegistry.ListAsync"/> but is the public
    /// "metadata" surface callers should prefer so audit / policy
    /// decorators see every metadata read.
    /// </summary>
    /// <param name="scope">The scope to list.</param>
    /// <param name="ownerId">The scope-specific owner id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, Guid? ownerId, CancellationToken ct);
}