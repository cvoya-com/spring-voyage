// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// A participant address enriched with a human-readable display name.
/// Used in <see cref="InboxItemResponse"/>, thread summaries, and thread
/// events wherever a raw address was previously returned. The
/// <see cref="Address"/> field always carries the canonical address so
/// callers can route, filter, or display the raw address in a metadata
/// popover; <see cref="DisplayName"/> is the resolved name from the
/// appropriate definition store (agent / unit) or the address path as a
/// fallback for unknown / deleted participants.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Address"/> field is intentionally heterogeneous in this
/// transitional state (#1490):
/// <list type="bullet">
///   <item><description>
///     <b>Agents and units</b> carry the identity form
///     <c>scheme:id:&lt;uuid&gt;</c> — unambiguous stable UUID, not a slug.
///   </description></item>
///   <item><description>
///     <b>Humans</b> still carry the navigation form
///     <c>human://&lt;username&gt;</c> until #1491 (human stable identity)
///     lands. The resolver picks the correct comparison strategy per scheme:
///     human addresses compare via <c>://</c>, agent/unit addresses compare
///     via <c>:id:</c>.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Callers checking "is this participant the currently logged-in user?"
/// should compare against <c>UserProfileResponse.Address</c>, which always
/// carries a <c>human://</c> form. Cross-scheme comparisons are inherently
/// false, so the heterogeneous mix never produces a false positive.
/// </para>
/// </remarks>
/// <param name="Address">
/// The canonical address string. For <c>agent://</c> and <c>unit://</c>
/// sources this is <c>scheme:id:&lt;uuid&gt;</c>; for <c>human://</c>
/// sources this is <c>human://&lt;username&gt;</c> (navigation form, until
/// #1491 lands).
/// </param>
/// <param name="DisplayName">
/// The human-readable display name. For <c>agent</c> sources this is
/// <c>AgentDefinition.Name</c>; for <c>unit</c> it is
/// <c>UnitDefinitionEntity.Name</c>; for <c>human</c> and any other
/// scheme it is the path component of the address (the user-id slug).
/// Never null — falls back to the path when resolution fails.
/// </param>
public record ParticipantRef(string Address, string DisplayName);