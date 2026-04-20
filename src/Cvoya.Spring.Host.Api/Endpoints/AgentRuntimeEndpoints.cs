// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps <c>/api/v1/agent-runtimes</c> — the per-tenant agent-runtime
/// install management surface. Runtimes are registered in DI via each
/// runtime package's <c>AddCvoyaSpringAgentRuntime*</c> extension; rows
/// in <c>tenant_agent_runtime_installs</c> determine which of those
/// runtimes are visible to a given tenant's wizard, CLI, and
/// unit-creation flows.
/// </summary>
public static class AgentRuntimeEndpoints
{
    /// <summary>
    /// Registers the agent-runtime install endpoints on the supplied
    /// route builder. Call site attaches <c>.RequireAuthorization()</c> on
    /// the returned group; every route here reads or writes
    /// tenant-scoped install data.
    /// </summary>
    public static RouteGroupBuilder MapAgentRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agent-runtimes")
            .WithTags("AgentRuntimes");

        group.MapGet("/", ListAsync)
            .WithName("ListInstalledAgentRuntimes")
            .WithSummary("List every agent runtime installed on the current tenant")
            .Produces<InstalledAgentRuntimeResponse[]>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetAsync)
            .WithName("GetInstalledAgentRuntime")
            .WithSummary("Get a single installed agent runtime by id")
            .Produces<InstalledAgentRuntimeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/models", GetModelsAsync)
            .WithName("GetInstalledAgentRuntimeModels")
            .WithSummary("Get the tenant's configured model list for an installed agent runtime")
            .Produces<AgentRuntimeModelResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/install", InstallAsync)
            .WithName("InstallAgentRuntime")
            .WithSummary("Install the runtime on the current tenant (idempotent)")
            .Produces<InstalledAgentRuntimeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}", UninstallAsync)
            .WithName("UninstallAgentRuntime")
            .WithSummary("Uninstall the runtime from the current tenant")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPatch("/{id}/config", UpdateConfigAsync)
            .WithName("UpdateAgentRuntimeConfig")
            .WithSummary("Replace the tenant-scoped configuration for an installed runtime")
            .Produces<InstalledAgentRuntimeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> ListAsync(
        [FromServices] ITenantAgentRuntimeInstallService installService,
        [FromServices] IAgentRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        var installs = await installService.ListAsync(cancellationToken);
        var rows = installs
            .Select(install => ToResponse(install, registry.Get(install.RuntimeId)))
            .Where(r => r is not null)
            .Cast<InstalledAgentRuntimeResponse>()
            .ToArray();
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetAsync(
        string id,
        [FromServices] ITenantAgentRuntimeInstallService installService,
        [FromServices] IAgentRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        var install = await installService.GetAsync(id, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }
        var response = ToResponse(install, registry.Get(install.RuntimeId));
        return response is null
            ? Results.Problem(
                detail: $"Agent runtime '{id}' is installed but no longer registered with the host.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(response);
    }

    private static async Task<IResult> GetModelsAsync(
        string id,
        [FromServices] ITenantAgentRuntimeInstallService installService,
        [FromServices] IAgentRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        var install = await installService.GetAsync(id, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var runtime = registry.Get(install.RuntimeId);
        // Build a { modelId -> descriptor } lookup from the runtime's seed
        // catalog so display-name + context-window come through for any
        // tenant-configured model the runtime still knows about. Unknown
        // ids (catalog drift: operator added a model the runtime removed)
        // surface with the id as the display name and no context window.
        var runtimeIndex = runtime?.DefaultModels
            .ToDictionary(m => m.Id, m => m, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase);

        var configured = install.Config.Models.Count > 0
            ? install.Config.Models
            : (IReadOnlyList<string>)(runtime?.DefaultModels.Select(m => m.Id).ToArray()
                ?? Array.Empty<string>());

        var response = configured
            .Select(modelId => runtimeIndex.TryGetValue(modelId, out var descriptor)
                ? new AgentRuntimeModelResponse(descriptor.Id, descriptor.DisplayName, descriptor.ContextWindow)
                : new AgentRuntimeModelResponse(modelId, modelId, null))
            .ToArray();
        return Results.Ok(response);
    }

    private static async Task<IResult> InstallAsync(
        string id,
        [FromBody] AgentRuntimeInstallRequest? body,
        [FromServices] ITenantAgentRuntimeInstallService installService,
        [FromServices] IAgentRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (registry.Get(id) is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        AgentRuntimeInstallConfig? config = body is null
            ? null
            : new AgentRuntimeInstallConfig(
                Models: body.Models ?? Array.Empty<string>(),
                DefaultModel: body.DefaultModel,
                BaseUrl: body.BaseUrl);

        var install = await installService.InstallAsync(id, config, cancellationToken);
        var response = ToResponse(install, registry.Get(install.RuntimeId));
        return response is null
            ? Results.Problem(
                detail: "Runtime was installed but could not be projected.",
                statusCode: StatusCodes.Status500InternalServerError)
            : Results.Ok(response);
    }

    private static async Task<IResult> UninstallAsync(
        string id,
        [FromServices] ITenantAgentRuntimeInstallService installService,
        CancellationToken cancellationToken)
    {
        await installService.UninstallAsync(id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateConfigAsync(
        string id,
        [FromBody] AgentRuntimeInstallConfig config,
        [FromServices] ITenantAgentRuntimeInstallService installService,
        [FromServices] IAgentRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (registry.Get(id) is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var install = await installService.UpdateConfigAsync(id, config, cancellationToken);
            var response = ToResponse(install, registry.Get(install.RuntimeId));
            return response is null
                ? Results.Problem(
                    detail: "Runtime config was updated but could not be projected.",
                    statusCode: StatusCodes.Status500InternalServerError)
                : Results.Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static InstalledAgentRuntimeResponse? ToResponse(
        InstalledAgentRuntime install,
        IAgentRuntime? runtime)
    {
        if (runtime is null)
        {
            // Orphan: install row exists but the runtime package is gone.
            // Surface with stub metadata so the operator can see the row
            // and remove it via DELETE; null-returns get filtered by the
            // list endpoint above.
            return null;
        }

        return new InstalledAgentRuntimeResponse(
            Id: install.RuntimeId,
            DisplayName: runtime.DisplayName,
            ToolKind: runtime.ToolKind,
            InstalledAt: install.InstalledAt,
            UpdatedAt: install.UpdatedAt,
            Models: install.Config.Models,
            DefaultModel: install.Config.DefaultModel,
            BaseUrl: install.Config.BaseUrl);
    }
}