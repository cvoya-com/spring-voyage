// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

/// <summary>
/// Resolves a human-readable display name for a wire-form participant
/// address. Post-#1629 the canonical wire form is
/// <c>scheme:&lt;32-hex-no-dash&gt;</c> (e.g. <c>agent:8c5fab…</c>); the
/// legacy navigation (<c>scheme://path</c>) and identity-form
/// (<c>scheme:id:&lt;uuid&gt;</c>) shapes are still accepted defensively
/// for activity events written under the old wire format.
///
/// <para>
/// <b>Non-empty contract (#1635).</b> Implementations MUST return a
/// non-empty string for every input. Resolution failures (deleted entity,
/// missing row, transient DB error) surface as the
/// <see cref="DeletedDisplayName"/> sentinel (<c>&lt;deleted&gt;</c>) so
/// callers — the portal's render path AND the Dapr prompt-assembly path
/// (#2129) — never have to fall back to a raw GUID.
/// </para>
///
/// <para>
/// Resolution sources by scheme:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>agent:&lt;guid&gt;</c> → <c>AgentDefinitions.DisplayName</c>.
///   </description></item>
///   <item><description>
///     <c>unit:&lt;guid&gt;</c> → <c>UnitDefinitions.DisplayName</c>.
///   </description></item>
///   <item><description>
///     <c>human:&lt;guid&gt;</c> →
///     <see cref="IHumanIdentityResolver.GetDisplayNameAsync"/>.
///   </description></item>
///   <item><description>
///     <c>tenant-user:&lt;guid&gt;</c> → <c>tenant_users.DisplayName</c>
///     (ADR-0047 §1; new actor kind, resolved in <c>Cvoya.Spring.Dapr</c>
///     against the <c>TenantUserEntity</c> table).
///   </description></item>
/// </list>
///
/// <para>
/// Implementations are scoped per-request and should cache lookups within
/// the request lifetime so repeated calls for the same address (e.g. the
/// same agent appearing on multiple inbox rows) round-trip the database
/// at most once.
/// </para>
///
/// <para>
/// <b>Why this lives in <c>Cvoya.Spring.Core</c>.</b> The interface is a
/// tenant-scoped data-projection helper with no Dapr / EF / Host.Api
/// dependencies. Both the portal endpoints (Host.Api) and the in-process
/// prompt-assembly path (Dapr) need it; placing the contract in Core
/// keeps the dependency direction Core ← Dapr ← Host.Api intact and lets
/// the cloud overlay register a tenant-aware variant via
/// <c>TryAddScoped</c> ahead of the OSS default.
/// </para>
/// </summary>
public interface IParticipantDisplayNameResolver
{
    /// <summary>
    /// Sentinel display name returned for participant addresses whose
    /// backing entity (agent / unit / human) is no longer present in the
    /// directory / humans table. Surfaces as a friendly tag in the
    /// portal — see #1630, #1635.
    /// </summary>
    public const string DeletedDisplayName = "<deleted>";

    /// <summary>
    /// Returns the display name for <paramref name="address"/>. Never
    /// returns an empty or whitespace string — see the type-level
    /// non-empty contract.
    /// </summary>
    /// <param name="address">
    /// A wire-form participant address (e.g. <c>agent:8c5fab2a…</c> or,
    /// for legacy events, <c>agent://ada</c>).
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    ValueTask<string> ResolveAsync(string address, CancellationToken cancellationToken = default);
}
