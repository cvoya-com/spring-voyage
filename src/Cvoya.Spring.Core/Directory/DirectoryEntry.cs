// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Directory;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an entry in the platform directory, mapping an
/// <see cref="Address"/> to its actor metadata. Identity is the entity
/// Guid carried inside <see cref="Address"/>; the canonical Dapr actor
/// key string is exposed as <see cref="ActorId"/> (the no-dash form of
/// <c>Address.Id</c>) for direct hand-off to <c>new ActorId(...)</c>.
/// </summary>
/// <param name="Address">The address of the registered component.</param>
/// <param name="ActorId">
/// The Dapr actor key — the canonical no-dash 32-char hex string form
/// of <see cref="Address"/>'s Guid identity. Equivalent to
/// <c>GuidFormatter.Format(Address.Id)</c>; carried separately on the
/// record because every routing call site hands this directly to
/// Dapr's <c>new ActorId(string)</c>.
/// </param>
/// <param name="DisplayName">The human-readable display name of the component.</param>
/// <param name="Description">A description of the component.</param>
/// <param name="Role">An optional role identifier used for multicast resolution (e.g., "backend-engineer").</param>
/// <param name="RegisteredAt">The timestamp when the component was registered.</param>
public record DirectoryEntry(
    Address Address,
    string ActorId,
    string DisplayName,
    string Description,
    string? Role,
    DateTimeOffset RegisteredAt)
{
    /// <summary>
    /// Convenience accessor for the Guid identity behind <see cref="ActorId"/>.
    /// Equal to <see cref="Address"/>'s Id; carried as a property so callers
    /// that prefer the typed Guid don't have to re-parse the string.
    /// </summary>
    public Guid Id => Address.Id;
}
