/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Request body for creating a new unit.
/// </summary>
/// <param name="Name">The unique name for the unit.</param>
/// <param name="DisplayName">A human-readable display name.</param>
/// <param name="Description">A description of the unit's purpose.</param>
public record CreateUnitRequest(
    string Name,
    string DisplayName,
    string Description);

/// <summary>
/// Response body representing a unit.
/// </summary>
/// <param name="Id">The unique actor identifier.</param>
/// <param name="Name">The unit's name (address path).</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">A description of the unit.</param>
/// <param name="RegisteredAt">The timestamp when the unit was registered.</param>
public record UnitResponse(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    DateTimeOffset RegisteredAt);

/// <summary>
/// Request body for adding a member to a unit.
/// </summary>
/// <param name="MemberAddress">The address of the member to add (e.g., agent://my-agent).</param>
public record AddMemberRequest(AddressDto MemberAddress);
