// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using Microsoft.AspNetCore.Authorization;

/// <summary>
/// Named authorization policies for unit-scoped permissions.
/// </summary>
public static class PermissionPolicies
{
    /// <summary>Policy name requiring at least Viewer permission.</summary>
    public const string UnitViewer = "UnitViewer";

    /// <summary>Policy name requiring at least Operator permission.</summary>
    public const string UnitOperator = "UnitOperator";

    /// <summary>Policy name requiring Owner permission.</summary>
    public const string UnitOwner = "UnitOwner";

    /// <summary>
    /// Registers the unit permission policies in the authorization options.
    /// </summary>
    /// <param name="options">The authorization options to configure.</param>
    public static void AddUnitPermissionPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(UnitViewer, policy =>
            policy.Requirements.Add(new PermissionRequirement(PermissionLevel.Viewer)));

        options.AddPolicy(UnitOperator, policy =>
            policy.Requirements.Add(new PermissionRequirement(PermissionLevel.Operator)));

        options.AddPolicy(UnitOwner, policy =>
            policy.Requirements.Add(new PermissionRequirement(PermissionLevel.Owner)));
    }
}