/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Directory;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an entry in the platform directory, mapping an address to metadata.
/// </summary>
/// <param name="Address">The address of the registered component.</param>
/// <param name="DisplayName">The human-readable display name of the component.</param>
/// <param name="Description">A description of the component.</param>
/// <param name="RegisteredAt">The timestamp when the component was registered.</param>
public record DirectoryEntry(
    Address Address,
    string DisplayName,
    string Description,
    DateTimeOffset RegisteredAt);
