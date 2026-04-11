// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Evaluates <see cref="PermissionRequirement"/> by resolving the authenticated user's
/// effective permission within the target unit and comparing it to the minimum required level.
/// The unit ID is extracted from the <c>id</c> route parameter.
/// </summary>
public class PermissionHandler(
    IPermissionService permissionService,
    IHttpContextAccessor httpContextAccessor,
    ILoggerFactory loggerFactory) : AuthorizationHandler<PermissionRequirement>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PermissionHandler>();

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Permission check failed: no user identifier in claims");
            return;
        }

        var httpContext = httpContextAccessor.HttpContext;
        var unitId = httpContext?.GetRouteValue("id")?.ToString();
        if (string.IsNullOrEmpty(unitId))
        {
            _logger.LogWarning("Permission check failed: no unit ID in route for user {UserId}", userId);
            return;
        }

        var permission = await permissionService.ResolvePermissionAsync(userId, unitId);
        if (permission is null)
        {
            _logger.LogWarning(
                "Permission check failed: user {UserId} has no permission in unit {UnitId}",
                userId, unitId);
            return;
        }

        if (permission.Value >= requirement.MinimumPermission)
        {
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogWarning(
                "Permission check failed: user {UserId} has {Actual} but {Required} is required in unit {UnitId}",
                userId, permission.Value, requirement.MinimumPermission, unitId);
        }
    }
}