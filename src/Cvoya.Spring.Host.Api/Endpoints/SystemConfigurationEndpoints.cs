// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Configuration;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Exposes the cached startup <see cref="ConfigurationReport"/> over HTTP.
/// Portal (<c>/system/configuration</c>) and CLI
/// (<c>spring system configuration</c>) both consume this endpoint so the two
/// surfaces can't drift on wording or grouping.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth.</b> Anonymous in the OSS build — the report does not contain
/// secret material (env-var names only, no values), and the anonymous About
/// panel / CLI verb need to work before a caller has negotiated a token.
/// The private cloud host can layer <c>RequireAuthorization()</c> by
/// overriding the route map or wrapping the endpoint in tenant-aware
/// middleware.
/// </para>
/// <para>
/// <b>Shape.</b> The response body IS the <see cref="ConfigurationReport"/>
/// record — no DTO shim. Keeping the wire shape identical to the in-memory
/// contract means CLI rendering can round-trip without a second model.
/// </para>
/// </remarks>
public static class SystemConfigurationEndpoints
{
    /// <summary>
    /// Registers the configuration-report endpoints on <paramref name="app"/>.
    /// </summary>
    public static RouteGroupBuilder MapSystemConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system")
            .WithTags("System");

        group.MapGet("/configuration", ([FromServices] IStartupConfigurationValidator validator) =>
            Results.Ok(validator.Report))
            .WithName("GetSystemConfigurationReport")
            .WithSummary("Return the cached startup configuration validation report")
            .Produces<ConfigurationReport>(StatusCodes.Status200OK);

        return group;
    }
}