// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISecretResolver"/> implementation: looks up the
/// opaque store key for a <see cref="SecretRef"/> in the registry and
/// then reads the plaintext from the store. The private cloud repo
/// wraps this with audit-log and RBAC decorators via DI.
///
/// <para>
/// Implements Unit → Tenant inheritance (ADR 0003): a
/// <see cref="SecretScope.Unit"/> request whose unit entry is missing
/// transparently falls through to the same-name tenant entry. The
/// fall-through is gated on <see cref="SecretsOptions.InheritTenantFromUnit"/>
/// so customers with strict-isolation requirements can opt out.
/// </para>
///
/// <para>
/// <b>Version pinning (wave 7 A5).</b> When a caller passes a specific
/// version to the pinning overload, both the direct and inherited
/// lookups use that version. A pinned read never silently returns a
/// different version — if neither the unit nor the tenant scope has
/// the requested version, the resolver returns
/// <see cref="SecretResolvePath.NotFound"/>. This preserves the
/// caller's intent: "show me v2" means v2 or nothing.
/// </para>
///
/// <para>
/// <b>RBAC contract for the fall-through path.</b>
/// <see cref="ISecretAccessPolicy"/> is consulted with
/// <see cref="SecretAccessAction.Read"/> at EVERY scope the resolver
/// touches — not just the originally requested scope. A caller with a
/// unit-level read grant but no tenant-level read grant receives
/// <see cref="SecretResolvePath.NotFound"/> on a fall-through, never a
/// silently-masked tenant plaintext. "Fail closed" is the behavior
/// under a denied policy; there is no fallback-to-allow-all branch.
/// </para>
/// </summary>
public class ComposedSecretResolver : ISecretResolver
{
    private readonly ISecretRegistry _registry;
    private readonly ISecretStore _store;
    private readonly ITenantContext _tenantContext;
    private readonly ISecretAccessPolicy _accessPolicy;
    private readonly IOptions<SecretsOptions> _options;

    /// <summary>
    /// Creates a new <see cref="ComposedSecretResolver"/>.
    /// </summary>
    public ComposedSecretResolver(
        ISecretRegistry registry,
        ISecretStore store,
        ITenantContext tenantContext,
        ISecretAccessPolicy accessPolicy,
        IOptions<SecretsOptions> options)
    {
        _registry = registry;
        _store = store;
        _tenantContext = tenantContext;
        _accessPolicy = accessPolicy;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(SecretRef @ref, CancellationToken ct)
    {
        var resolution = await ResolveWithPathAsync(@ref, version: null, ct);
        return resolution.Value;
    }

    /// <inheritdoc />
    public Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, CancellationToken ct)
        => ResolveWithPathAsync(@ref, version: null, ct);

    /// <inheritdoc />
    public async Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, int? version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(@ref);

        // Access-policy check at the requested scope. A denial here short-
        // circuits before the registry is touched — the audit path still
        // sees the attempt via the decorator wrapping this resolver.
        if (!await _accessPolicy.IsAuthorizedAsync(
            SecretAccessAction.Read, @ref.Scope, @ref.OwnerId, ct))
        {
            return new SecretResolution(null, SecretResolvePath.NotFound, null, null);
        }

        // Direct lookup at the requested scope, at the requested version
        // (or latest when version is null).
        var (direct, directVersion) = await TryReadAtVersionAsync(@ref, version, ct);
        if (direct is not null)
        {
            return new SecretResolution(direct, SecretResolvePath.Direct, @ref, directVersion);
        }

        // Unit → Tenant fall-through. Only fires for unit-scope requests;
        // tenant and platform resolves never inherit in the opposite
        // direction. Gated by configuration so customers with strict-
        // isolation requirements can opt out.
        if (@ref.Scope != SecretScope.Unit || !_options.Value.InheritTenantFromUnit)
        {
            return new SecretResolution(null, SecretResolvePath.NotFound, null, null);
        }

        var tenantRef = new SecretRef(
            SecretScope.Tenant,
            _tenantContext.CurrentTenantId,
            @ref.Name);

        // Access-policy check at the tenant scope. This is the critical
        // "no privilege escalation via inheritance" guard: without a
        // tenant-level read grant, the unit caller cannot observe the
        // tenant value even if the name matches.
        if (!await _accessPolicy.IsAuthorizedAsync(
            SecretAccessAction.Read, tenantRef.Scope, tenantRef.OwnerId, ct))
        {
            return new SecretResolution(null, SecretResolvePath.NotFound, null, null);
        }

        // Fall-through uses the SAME version pin. A caller asking for
        // (Unit, u, name, v=3) that misses at the unit scope ONLY finds
        // the tenant row if the tenant also has version 3 — never a
        // silently-different version.
        var (inherited, inheritedVersion) = await TryReadAtVersionAsync(tenantRef, version, ct);
        if (inherited is not null)
        {
            return new SecretResolution(inherited, SecretResolvePath.InheritedFromTenant, tenantRef, inheritedVersion);
        }

        return new SecretResolution(null, SecretResolvePath.NotFound, null, null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, string ownerId, CancellationToken ct)
        => _registry.ListAsync(scope, ownerId, ct);

    private async Task<(string? Value, int? Version)> TryReadAtVersionAsync(
        SecretRef @ref, int? version, CancellationToken ct)
    {
        // When no version pin is supplied we intentionally call the
        // legacy single-argument overload so audit decorators and test
        // stubs written against the A4-era signature keep seeing the
        // same call shape. Only version-pinned resolves route to the
        // two-argument overload.
        var lookup = version is null
            ? await _registry.LookupWithVersionAsync(@ref, ct)
            : await _registry.LookupWithVersionAsync(@ref, version, ct);
        if (lookup is null)
        {
            return (null, null);
        }

        var plaintext = await _store.ReadAsync(lookup.Value.Pointer.StoreKey, ct);
        return (plaintext, lookup.Value.Version);
    }
}