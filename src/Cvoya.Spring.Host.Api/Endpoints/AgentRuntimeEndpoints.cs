// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.CredentialHealth;
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

        group.MapPost("/{id}/validate-credential", ValidateCredentialAsync)
            .WithName("ValidateAgentRuntimeCredential")
            .WithSummary("Validate a candidate credential against the runtime's backing service; records the outcome in the credential-health store")
            .Produces<CredentialValidateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/credential-health", GetCredentialHealthAsync)
            .WithName("GetAgentRuntimeCredentialHealth")
            .WithSummary("Get the current credential-health row for a runtime on the current tenant")
            .Produces<CredentialHealthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/verify-baseline", VerifyBaselineAsync)
            .WithName("VerifyAgentRuntimeBaseline")
            .WithSummary("Invoke the runtime's VerifyContainerBaselineAsync and return the result")
            .Produces<ContainerBaselineCheckResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/refresh-models", RefreshModelsAsync)
            .WithName("RefreshAgentRuntimeModels")
            .WithSummary("Best-effort live-catalog lookup; replaces the tenant's configured model list on success")
            .Produces<InstalledAgentRuntimeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

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

    private static async Task<IResult> ValidateCredentialAsync(
        string id,
        [FromBody] CredentialValidateRequest body,
        [FromServices] IAgentRuntimeRegistry registry,
        [FromServices] ICredentialHealthStore credentialHealthStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var runtime = registry.Get(id);
        if (runtime is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var secretName = string.IsNullOrWhiteSpace(body.SecretName) ? "default" : body.SecretName;
        var result = await runtime.ValidateCredentialAsync(body.Credential ?? string.Empty, cancellationToken);
        var persistent = MapToHealth(result.Status);

        // NetworkError is a per-attempt signal; don't flip the persistent
        // row on transient transport failures. Every other outcome writes
        // so the accept-time record reflects the latest check.
        if (result.Status != CredentialValidationStatus.NetworkError)
        {
            await credentialHealthStore.RecordAsync(
                CredentialHealthKind.AgentRuntime,
                runtime.Id,
                secretName,
                persistent,
                lastError: result.ErrorMessage,
                cancellationToken);
        }

        return Results.Ok(new CredentialValidateResponse(
            Valid: result.Valid,
            Status: persistent,
            ErrorMessage: result.ErrorMessage));
    }

    private static async Task<IResult> GetCredentialHealthAsync(
        string id,
        [FromServices] IAgentRuntimeRegistry registry,
        [FromServices] ICredentialHealthStore credentialHealthStore,
        [FromQuery] string? secretName,
        CancellationToken cancellationToken)
    {
        if (registry.Get(id) is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var resolvedSecret = string.IsNullOrWhiteSpace(secretName) ? "default" : secretName;
        var row = await credentialHealthStore.GetAsync(
            CredentialHealthKind.AgentRuntime, id, resolvedSecret, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"No credential-health row recorded for agent runtime '{id}' / '{resolvedSecret}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new CredentialHealthResponse(
            SubjectId: row.SubjectId,
            SecretName: row.SecretName,
            Status: row.Status,
            LastError: row.LastError,
            LastChecked: row.LastChecked));
    }

    private static CredentialHealthStatus MapToHealth(CredentialValidationStatus status) => status switch
    {
        CredentialValidationStatus.Valid => CredentialHealthStatus.Valid,
        CredentialValidationStatus.Invalid => CredentialHealthStatus.Invalid,
        CredentialValidationStatus.NetworkError => CredentialHealthStatus.Unknown,
        _ => CredentialHealthStatus.Unknown,
    };

    private static async Task<IResult> VerifyBaselineAsync(
        string id,
        [FromServices] IAgentRuntimeRegistry registry,
        CancellationToken cancellationToken)
    {
        var runtime = registry.Get(id);
        if (runtime is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var result = await runtime.VerifyContainerBaselineAsync(cancellationToken);
        return Results.Ok(new ContainerBaselineCheckResponse(
            RuntimeId: runtime.Id,
            Passed: result.Passed,
            Errors: result.Errors));
    }

    private static async Task<IResult> RefreshModelsAsync(
        string id,
        [FromBody] AgentRuntimeRefreshModelsRequest? body,
        [FromServices] IAgentRuntimeRegistry registry,
        [FromServices] ITenantAgentRuntimeInstallService installService,
        CancellationToken cancellationToken)
    {
        var runtime = registry.Get(id);
        if (runtime is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var install = await installService.GetAsync(id, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Agent runtime '{id}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var credential = body?.Credential ?? string.Empty;
        var fetch = await runtime.FetchLiveModelsAsync(credential, cancellationToken);

        if (fetch.Status is not FetchLiveModelsStatus.Success)
        {
            // Map non-success statuses to HTTP problem responses. 502 is
            // the correct signal for both transient transport failures
            // and "runtime cannot enumerate" outcomes: the caller asked
            // the platform to reach through to a provider, and the
            // provider did not cooperate. Invalid credential flips to
            // 401 so the wizard can distinguish "fix your key" from
            // "try again later".
            var (statusCode, prefix) = fetch.Status switch
            {
                FetchLiveModelsStatus.InvalidCredential => (StatusCodes.Status401Unauthorized, "Credential rejected"),
                FetchLiveModelsStatus.Unsupported => (StatusCodes.Status502BadGateway, "Live catalog not supported"),
                FetchLiveModelsStatus.NetworkError => (StatusCodes.Status502BadGateway, "Upstream fetch failed"),
                _ => (StatusCodes.Status502BadGateway, "Unknown fetch outcome"),
            };
            return Results.Problem(
                title: prefix,
                detail: fetch.ErrorMessage ?? prefix,
                statusCode: statusCode);
        }

        // Replace the tenant's stored model list with the live catalog.
        // DefaultModel is preserved if still present in the new list;
        // otherwise we pick the first entry so the tenant never ends up
        // with a DefaultModel id that no longer exists. BaseUrl stays
        // untouched — refresh is about the catalog, not the endpoint.
        var liveIds = fetch.Models.Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        var existingDefault = install.Config.DefaultModel;
        var preservedDefault = existingDefault is not null
            && liveIds.Any(id => string.Equals(id, existingDefault, StringComparison.OrdinalIgnoreCase));
        var nextDefault = preservedDefault
            ? existingDefault
            : (liveIds.Length > 0 ? liveIds[0] : null);

        var nextConfig = new AgentRuntimeInstallConfig(
            Models: liveIds,
            DefaultModel: nextDefault,
            BaseUrl: install.Config.BaseUrl);

        try
        {
            var updated = await installService.UpdateConfigAsync(id, nextConfig, cancellationToken);
            var response = ToResponse(updated, runtime);
            return response is null
                ? Results.Problem(
                    detail: "Live-model catalog was fetched but the install row could not be projected.",
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