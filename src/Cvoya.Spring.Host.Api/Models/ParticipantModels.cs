// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// A participant address enriched with a human-readable display name.
/// Used in <see cref="InboxItemResponse"/>, thread summaries, and thread
/// events wherever a bare <c>scheme://path</c> address was previously
/// returned. The <see cref="Address"/> field always carries the canonical
/// address so callers can route, filter, or display the raw address in a
/// metadata popover; <see cref="DisplayName"/> is the resolved name from the
/// appropriate definition store (agent / unit) or the address path as a
/// fallback for unknown / deleted participants.
/// </summary>
/// <param name="Address">The canonical <c>scheme://path</c> address string.</param>
/// <param name="DisplayName">
/// The human-readable display name. For <c>agent://</c> sources this is
/// <c>AgentDefinition.Name</c>; for <c>unit://</c> it is
/// <c>UnitDefinitionEntity.Name</c>; for <c>human://</c> and any other
/// scheme it is the path component of the address (the user-id slug).
/// Never null — falls back to the path when resolution fails.
/// </param>
public record ParticipantRef(string Address, string DisplayName);