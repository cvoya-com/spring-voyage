// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// A participant address paired with a server-resolved human-readable
/// display name. Used in <see cref="InboxItemResponse"/>, thread summaries,
/// and thread events wherever a raw address would otherwise leak into the
/// UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire shape (post-#1629).</b> <see cref="Address"/> always carries
/// the canonical <c>scheme:&lt;32-hex-no-dash&gt;</c> form (e.g.
/// <c>agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7</c>). The legacy
/// <c>scheme://path</c> and <c>scheme:id:&lt;uuid&gt;</c> shapes were
/// retired with the single-identity baseline; the resolver still accepts
/// them on the read side for activity events persisted before the
/// baseline migration.
/// </para>
/// <para>
/// <b>Non-empty <see cref="DisplayName"/> (#1635).</b> The server resolves
/// every Guid identity on the way out — agents and units against their
/// definition rows, humans via
/// <see cref="Cvoya.Spring.Core.Security.IHumanIdentityResolver"/>. When
/// the entity has been deleted (or never existed) the resolver returns
/// the literal <c>&lt;deleted&gt;</c> placeholder rather than blanking
/// the field. This contract lets the portal render <c>DisplayName</c>
/// directly without UUID-shape heuristics.
/// </para>
/// <para>
/// Callers checking "is this participant the currently logged-in user?"
/// should compare the canonical address string against
/// <see cref="UserProfileResponse.Address"/>; cross-scheme comparisons
/// are inherently false.
/// </para>
/// </remarks>
/// <param name="Address">
/// The canonical wire-form participant address —
/// <c>scheme:&lt;32-hex-no-dash&gt;</c> per #1629. May still surface in
/// legacy <c>scheme://path</c> form on threads that include events
/// persisted before the baseline migration.
/// </param>
/// <param name="DisplayName">
/// The server-resolved human-readable display name. Always non-empty.
/// Falls back to <c>&lt;deleted&gt;</c> when the underlying entity has
/// been removed; the portal can render this string directly.
/// </param>
public record ParticipantRef(string Address, string DisplayName);