// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Evaluates <see cref="PermissionRequirement"/> by resolving the
/// authenticated caller's effective permission within the target unit
/// and comparing it to the minimum required level. Per ADR-0047 §1 and
/// #2768 the OSS deployment surfaces an authenticated caller as the
/// canonical operator <see cref="OssTenantUserIds.Operator"/> tenant-user
/// address; <see cref="IPermissionService.ResolveEffectivePermissionAsync"/>
/// short-circuits that to implicit Owner.
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
        if (context.User.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning("Permission check failed: caller is not authenticated");
            return;
        }

        var httpContext = httpContextAccessor.HttpContext;
        var unitIdRaw = httpContext?.GetRouteValue("id")?.ToString();
        if (string.IsNullOrEmpty(unitIdRaw))
        {
            _logger.LogWarning("Permission check failed: no unit ID in route");
            return;
        }

        if (!GuidFormatter.TryParse(unitIdRaw, out var unitGuid))
        {
            _logger.LogWarning(
                "Permission check failed: unit id {UnitId} is not a valid Guid",
                unitIdRaw);
            return;
        }

        // #2768: the OSS-default IAuthenticatedCallerAccessor surfaces
        // every authenticated caller as the operator tenant-user. The
        // declarative authorisation pipeline runs OUTSIDE the handler
        // graph that the accessor relies on (no HttpContext-bound
        // accessor injection here), so we materialise the same caller
        // address directly from the principal: any authenticated user
        // resolves to the operator in OSS. Cloud overlays replace this
        // handler via DI when they wire per-tenant-user permissions.
        var caller = Address.ForIdentity(Address.TenantUserScheme, OssTenantUserIds.Operator);

        // Hierarchy-aware check (#414): ancestor grants cascade down to
        // descendant units by default. An explicit direct grant on the
        // target unit still wins; units marked Isolated stop the ancestor
        // walk. See IPermissionService.ResolveEffectivePermissionAsync.
        var permission = await permissionService.ResolveEffectivePermissionAsync(caller, unitGuid);
        if (permission is null)
        {
            _logger.LogWarning(
                "Permission check failed: caller {Caller} has no effective permission in unit {UnitId}",
                caller, unitGuid);
            return;
        }

        if (permission.Value >= requirement.MinimumPermission)
        {
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogWarning(
                "Permission check failed: caller {Caller} has {Actual} but {Required} is required in unit {UnitId}",
                caller, permission.Value, requirement.MinimumPermission, unitGuid);
        }
    }
}
