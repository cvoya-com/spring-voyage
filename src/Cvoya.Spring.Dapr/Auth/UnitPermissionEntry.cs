// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Represents a human's permission entry within a unit, including identity and notification preferences.
/// </summary>
/// <param name="HumanId">The unique identifier of the human.</param>
/// <param name="Permission">The permission level granted to this human within the unit.</param>
/// <param name="Identity">An optional display name or identity string for the human.</param>
/// <param name="Notifications">Whether this human receives notifications from the unit.</param>
public record UnitPermissionEntry(
    string HumanId,
    PermissionLevel Permission,
    string? Identity = null,
    bool Notifications = true);