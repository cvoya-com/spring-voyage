// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Directory;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an entry in the platform directory, mapping an
/// <see cref="Address"/> to its actor metadata. Identity is the entity
/// Guid <see cref="ActorId"/>, the same value carried inside
/// <see cref="Address"/>'s <see cref="Address.Id"/>. There is no
/// string-typed identity field at this layer — Dapr's
/// <c>new ActorId(string)</c> takes <c>GuidFormatter.Format(actorId)</c>
/// at the call site.
/// </summary>
/// <param name="Address">The address of the registered component.</param>
/// <param name="ActorId">
/// The Guid identity of the directory entry. Equal to
/// <see cref="Address"/>.<see cref="Address.Id"/>; carried explicitly
/// on the record so callers can read the typed identity without
/// reaching through <c>Address</c>.
/// </param>
/// <param name="DisplayName">The human-readable display name of the component.</param>
/// <param name="Description">A description of the component.</param>
/// <param name="Role">An optional role identifier used for multicast resolution (e.g., "backend-engineer").</param>
/// <param name="RegisteredAt">The timestamp when the component was registered.</param>
public record DirectoryEntry(
    Address Address,
    Guid ActorId,
    string DisplayName,
    string Description,
    string? Role,
    DateTimeOffset RegisteredAt);
