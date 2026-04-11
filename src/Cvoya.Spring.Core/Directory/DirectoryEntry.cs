// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Directory;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an entry in the platform directory, mapping an address to metadata.
/// </summary>
/// <param name="Address">The address of the registered component.</param>
/// <param name="ActorId">The Dapr actor identifier used to invoke this component.</param>
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
    DateTimeOffset RegisteredAt);