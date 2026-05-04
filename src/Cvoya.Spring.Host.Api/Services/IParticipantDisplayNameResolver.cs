// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

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
/// missing row, transient DB error) surface as
/// <see cref="ParticipantDisplayNameResolver.DeletedDisplayName"/>
/// (<c>&lt;deleted&gt;</c>) so the portal never has to fall back to a
/// raw GUID. The portal can drop its <c>looksLikeUuid</c> heuristic once
/// every <c>ParticipantRef</c> on the wire carries a non-empty
/// <c>DisplayName</c>.
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
///     <see cref="Cvoya.Spring.Core.Security.IHumanIdentityResolver.GetDisplayNameAsync"/>.
///   </description></item>
/// </list>
///
/// <para>
/// Implementations are scoped per-request and should cache lookups within
/// the request lifetime so repeated calls for the same address (e.g. the
/// same agent appearing on multiple inbox rows) round-trip the database
/// at most once.
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
}