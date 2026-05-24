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
/// missing row, transient DB error) surface as a per-scheme generic
/// fallback (#2532 / #2533) — <c>"an agent"</c>, <c>"a unit"</c>,
/// <c>"a connector"</c>, <c>"someone"</c>, <c>"a member"</c>, or
/// <c>"a {scheme}"</c> for unknown schemes — so callers never have to
/// fall back to a raw GUID and the UI voice stays conversational. Use
/// <see cref="ResolveStatusAsync"/> when the caller needs to distinguish
/// a real name from a fallback (e.g. to prefer a thread-level snapshot
/// captured before the entity was deleted).
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
///     <c>connector:&lt;guid&gt;</c> →
///     <c>ConnectorDefinitions.DisplayName</c>, falling back to
///     <c>"a {Type} connector"</c> when the row exists but has no
///     display name (e.g. <c>"a github connector"</c>).
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

    /// <summary>
    /// Returns the display name for <paramref name="address"/> together
    /// with a flag indicating whether the value is a per-scheme generic
    /// fallback (e.g. <c>"an agent"</c>, <c>"a connector"</c>) rather
    /// than the entity's real display name. Callers that maintain a
    /// thread-level snapshot of last-known names use this to decide
    /// whether to prefer the snapshot over the live resolution (#2533).
    /// </summary>
    /// <param name="address">A wire-form participant address.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    ValueTask<ParticipantDisplayName> ResolveStatusAsync(string address, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IParticipantDisplayNameResolver.ResolveStatusAsync"/>:
/// the user-facing <paramref name="DisplayName"/> plus a flag signalling
/// whether the value is a per-scheme generic fallback. The fallback flag
/// lets snapshot-aware callers (the thread-endpoints enrichment path)
/// substitute a previously-cached real name when the live row has been
/// soft-deleted (#2533).
/// </summary>
/// <param name="DisplayName">
/// Non-empty user-facing label for the address. When the underlying
/// entity has a real display name this is that name; otherwise this is
/// the per-scheme generic fallback ("an agent", "a connector", …).
/// </param>
/// <param name="IsFallback">
/// <c>true</c> when <paramref name="DisplayName"/> is a per-scheme
/// generic fallback rather than the entity's real display name. Always
/// <c>false</c> when the entity row exists with a non-empty display
/// name; always <c>true</c> when the row is missing / soft-deleted /
/// unparseable.
/// </param>
public readonly record struct ParticipantDisplayName(string DisplayName, bool IsFallback);
