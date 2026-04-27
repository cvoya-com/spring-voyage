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
/// <b>Auth.</b> Gated to <c>PlatformOperator</c> per the v0.1 role taxonomy
/// (see <c>docs/architecture/web-api.md</c>). The configuration report is
/// platform-operator information — env-var names, startup probe results —
/// and the principle "if the CLI uses it, it lives on the public API"
/// applies: the <c>spring system configuration</c> verb authenticates and
/// then reads. There is no anonymous surface for this endpoint in the OSS
/// build.
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
        var group = app.MapGroup("/api/v1/platform/system")
            .WithTags("System");

        group.MapGet("/configuration", ([FromServices] IStartupConfigurationValidator validator) =>
            Results.Ok(validator.Report))
            .WithName("GetSystemConfigurationReport")
            .WithSummary("Return the cached startup configuration validation report")
            .Produces<ConfigurationReport>(StatusCodes.Status200OK);

        return group;
    }
}