// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Dapr.Actors;

using Microsoft.AspNetCore.Authorization;

/// <summary>
/// An authorization requirement that specifies the minimum <see cref="PermissionLevel"/>
/// a human must have within a unit to access the resource.
/// </summary>
public class PermissionRequirement(PermissionLevel minimumPermission) : IAuthorizationRequirement
{
    /// <summary>
    /// Gets the minimum permission level required.
    /// </summary>
    public PermissionLevel MinimumPermission { get; } = minimumPermission;
}