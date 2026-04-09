/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body representing a directory entry.
/// </summary>
/// <param name="Address">The address of the registered component.</param>
/// <param name="ActorId">The actor identifier.</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">A description of the component.</param>
/// <param name="Role">The role identifier, if any.</param>
/// <param name="RegisteredAt">The timestamp when the component was registered.</param>
public record DirectoryEntryResponse(
    AddressDto Address,
    string ActorId,
    string DisplayName,
    string Description,
    string? Role,
    DateTimeOffset RegisteredAt);
