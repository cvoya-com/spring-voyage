// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Request body for creating a new unit.
/// </summary>
/// <param name="Name">The unique name for the unit.</param>
/// <param name="DisplayName">A human-readable display name.</param>
/// <param name="Description">A description of the unit's purpose.</param>
/// <param name="Model">An optional model identifier hint (e.g., default LLM).</param>
/// <param name="Color">An optional UI color hint used by the dashboard.</param>
public record CreateUnitRequest(
    string Name,
    string DisplayName,
    string Description,
    string? Model = null,
    string? Color = null);

/// <summary>
/// Request body for updating mutable unit metadata. All fields are optional;
/// <c>null</c> means "leave the existing value untouched".
/// </summary>
/// <param name="DisplayName">The new display name, or <c>null</c> to leave unchanged.</param>
/// <param name="Description">The new description, or <c>null</c> to leave unchanged.</param>
/// <param name="Model">The new model hint, or <c>null</c> to leave unchanged.</param>
/// <param name="Color">The new UI color hint, or <c>null</c> to leave unchanged.</param>
public record UpdateUnitRequest(
    string? DisplayName = null,
    string? Description = null,
    string? Model = null,
    string? Color = null);

/// <summary>
/// Response body representing a unit.
/// </summary>
/// <param name="Id">The unique actor identifier.</param>
/// <param name="Name">The unit's name (address path).</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">A description of the unit.</param>
/// <param name="RegisteredAt">The timestamp when the unit was registered.</param>
/// <param name="Status">The current lifecycle status of the unit.</param>
/// <param name="Model">An optional model identifier hint, if set.</param>
/// <param name="Color">An optional UI color hint, if set.</param>
public record UnitResponse(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    DateTimeOffset RegisteredAt,
    UnitStatus Status,
    string? Model,
    string? Color);

/// <summary>
/// Request body for adding a member to a unit.
/// </summary>
/// <param name="MemberAddress">The address of the member to add (e.g., agent://my-agent).</param>
public record AddMemberRequest(AddressDto MemberAddress);

/// <summary>
/// Request body for setting a human's permission level within a unit.
/// </summary>
/// <param name="Permission">The permission level (Viewer, Operator, Owner).</param>
/// <param name="Identity">An optional display name or identity string for the human.</param>
/// <param name="Notifications">Whether this human receives notifications. Defaults to true.</param>
public record SetHumanPermissionRequest(
    string Permission,
    string? Identity = null,
    bool? Notifications = null);