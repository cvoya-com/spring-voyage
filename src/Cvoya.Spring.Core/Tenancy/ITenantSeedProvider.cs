// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Contract for components that contribute startup seed data to a freshly
/// bootstrapped tenant. Implementations are discovered through dependency
/// injection (registered as <see cref="ITenantSeedProvider"/>) and invoked
/// once per host startup by the platform's default-tenant bootstrap hosted
/// service (see <c>Cvoya.Spring.Dapr.Tenancy.DefaultTenantBootstrapService</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifecycle.</strong> The bootstrap service iterates every
/// registered <see cref="ITenantSeedProvider"/> in ascending
/// <see cref="Priority"/> order and awaits
/// <see cref="ApplySeedsAsync(string, System.Threading.CancellationToken)"/>
/// on each one. The full pass runs synchronously inside
/// <c>IHostedService.StartAsync</c>; a provider that throws aborts the
/// bootstrap (and therefore host start) to surface the failure
/// loudly. Providers are NOT re-invoked at runtime — adding new seed
/// rows after startup is the seed provider's own responsibility (e.g. a
/// migration). Re-registering the seed provider in the next deployment
/// is the canonical way to deliver new seeds.
/// </para>
/// <para>
/// <strong>Idempotency contract.</strong> Implementations MUST be
/// idempotent — every call after the first MUST be a no-op against
/// rows the provider itself owns. The bootstrap service runs on every
/// host startup, including restarts of the same deployment, so a
/// provider that re-inserts on each call would duplicate rows and (on
/// the second run) violate the natural-key uniqueness the OSS schema
/// pins. Implementations MUST upsert by a <c>(tenant_id,
/// &lt;natural-key&gt;)</c> pair owned by the provider, and MUST NOT
/// overwrite columns the operator may have edited after the seed
/// landed (description text, custom labels, policy overrides, …). When
/// in doubt: treat the seed as initial data, not as a source of truth
/// — the operator wins after the first install.
/// </para>
/// <para>
/// <strong>Priority ordering.</strong> Providers run in ascending
/// <see cref="Priority"/> (lower runs first). When two providers share
/// a priority the bootstrap service falls back to the registration
/// order of the underlying DI descriptor — but seed providers SHOULD
/// declare a deliberate priority so a private-cloud overlay can slot
/// between two OSS defaults without depending on registration order.
/// Recommended slots: 0–99 platform infrastructure (skill bundles,
/// connector types), 100–199 platform content (default policies,
/// directory entries), 200–299 private overlays. Pick a slot that
/// leaves room on either side.
/// </para>
/// <para>
/// <strong>Logging contract.</strong> Implementations MUST log every
/// seed action at <c>LogLevel.Information</c> with the tenant id, the
/// provider <see cref="Id"/>, and a stable verb (e.g. <c>seeded</c>,
/// <c>skipped</c>) so operators can correlate the bootstrap log with
/// the resulting database state. Log <c>skipped</c> when the
/// idempotency check short-circuits the work — that signal is what
/// confirms the contract holds in production.
/// </para>
/// <para>
/// <strong>Extensibility.</strong> The interface lives in
/// <c>Cvoya.Spring.Core.Tenancy</c> alongside the rest of the tenant
/// surface, so the private cloud repo can register additional providers
/// (per-tenant runtime installs, license-gated content, BYOK seeds)
/// without taking a dependency on <c>Cvoya.Spring.Dapr</c>. Use
/// <c>services.AddSingleton&lt;ITenantSeedProvider, MyProvider&gt;()</c>
/// when registering — the bootstrap service enumerates the full
/// <c>IEnumerable&lt;ITenantSeedProvider&gt;</c> and respects DI
/// composition rules.
/// </para>
/// </remarks>
public interface ITenantSeedProvider
{
    /// <summary>
    /// Stable, human-readable identifier for this seed provider. Used
    /// in audit logs and operator-facing diagnostics so a failed seed
    /// can be traced back to the offending component without reflection
    /// on the implementation type. Conventionally a short kebab-case
    /// noun (e.g. <c>"skill-bundles"</c>, <c>"agent-runtimes"</c>);
    /// MUST be unique across the registered set so the bootstrap log is
    /// unambiguous.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Priority slot used by the bootstrap service to order seed
    /// providers. Lower values run first. See the type-level remarks
    /// for the recommended ranges.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Applies the provider's seeds to the given tenant. The tenant
    /// row already exists when this method is called — providers do
    /// NOT need to create it. Implementations MUST be idempotent (see
    /// type-level remarks) and SHOULD log every action at
    /// <c>Information</c> level.
    /// </summary>
    /// <param name="tenantId">Identifier of the tenant being seeded.
    /// Never null or whitespace.</param>
    /// <param name="cancellationToken">Cancellation token forwarded
    /// from the host's <c>StartAsync</c>.</param>
    Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken);
}