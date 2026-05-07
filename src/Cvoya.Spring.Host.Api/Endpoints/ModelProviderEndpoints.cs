// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.ModelProviders;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Maps <c>/api/v1/tenant/model-providers/installs</c> — the per-tenant
/// model-provider install management surface (ADR-0038). Providers are
/// declared in <c>platform/runtime-catalog.yaml</c>; rows in
/// <c>tenant_agent_runtime_installs</c> (re-keyed on provider id under
/// ADR-0038) determine which providers are visible to a given tenant's
/// wizard, CLI, and unit-creation flows.
/// </summary>
public static class ModelProviderEndpoints
{
    /// <summary>
    /// Registers the model-provider install endpoints on the supplied
    /// route builder. Call site attaches <c>.RequireAuthorization()</c>
    /// on the returned group; every route here reads or writes
    /// tenant-scoped install data.
    /// </summary>
    public static RouteGroupBuilder MapModelProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/model-providers/installs")
            .WithTags("ModelProviders");

        group.MapGet("/", ListAsync)
            .WithName("ListInstalledModelProviders")
            .WithSummary("List every model provider installed on the current tenant")
            .Produces<InstalledModelProviderResponse[]>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetAsync)
            .WithName("GetInstalledModelProvider")
            .WithSummary("Get a single installed model provider by id")
            .Produces<InstalledModelProviderResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/models", GetModelsAsync)
            .WithName("GetInstalledModelProviderModels")
            .WithSummary("Get the tenant's configured model list for an installed provider")
            .Produces<ModelProviderModelResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/install", InstallAsync)
            .WithName("InstallModelProvider")
            .WithSummary("Install the provider on the current tenant (idempotent)")
            .Produces<InstalledModelProviderResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}", UninstallAsync)
            .WithName("UninstallModelProvider")
            .WithSummary("Uninstall the provider from the current tenant")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPatch("/{id}/config", UpdateConfigAsync)
            .WithName("UpdateModelProviderConfig")
            .WithSummary("Replace the tenant-scoped configuration for an installed provider")
            .Produces<InstalledModelProviderResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/config", GetConfigAsync)
            .WithName("GetModelProviderConfig")
            .WithSummary("Get the tenant-scoped configuration slot for an installed provider")
            .Produces<ModelProviderConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/credential-health", GetCredentialHealthAsync)
            .WithName("GetModelProviderCredentialHealth")
            .WithSummary("Get the current credential-health row for a provider on the current tenant")
            .Produces<CredentialHealthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/validate-credential", ValidateCredentialAsync)
            .WithName("ValidateModelProviderCredential")
            .WithSummary("Probe the provider with the supplied credential; record the outcome in the credential-health store; does not touch the model catalogue")
            .Produces<ModelProviderValidateCredentialResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/refresh-models", RefreshModelsAsync)
            .WithName("RefreshModelProviderModels")
            .WithSummary("Best-effort live-catalogue lookup; replaces the tenant's configured model list on success")
            .Produces<InstalledModelProviderResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return group;
    }

    private static async Task<IResult> ListAsync(
        [FromServices] ITenantModelProviderInstallService installService,
        [FromServices] IRuntimeCatalog catalog,
        CancellationToken cancellationToken)
    {
        var installs = await installService.ListAsync(cancellationToken);
        var rows = installs
            .Select(install => ToResponse(install, catalog.GetModelProvider(install.ProviderId)))
            .Where(r => r is not null)
            .Cast<InstalledModelProviderResponse>()
            .ToArray();
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetAsync(
        string id,
        [FromServices] ITenantModelProviderInstallService installService,
        [FromServices] IRuntimeCatalog catalog,
        CancellationToken cancellationToken)
    {
        var install = await installService.GetAsync(id, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }
        var response = ToResponse(install, catalog.GetModelProvider(install.ProviderId));
        return response is null
            ? Results.Problem(
                detail: $"Model provider '{id}' is installed but no longer registered with the host.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(response);
    }

    private static async Task<IResult> GetModelsAsync(
        string id,
        [FromServices] ITenantModelProviderInstallService installService,
        [FromServices] IRuntimeCatalog catalog,
        CancellationToken cancellationToken)
    {
        var install = await installService.GetAsync(id, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var provider = catalog.GetModelProvider(install.ProviderId);
        var seedIds = provider?.DefaultModels ?? Array.Empty<string>();
        var configured = install.Config.Models.Count > 0
            ? install.Config.Models
            : seedIds;

        var response = configured
            .Select(modelId => new ModelProviderModelResponse(modelId, modelId, null))
            .ToArray();
        return Results.Ok(response);
    }

    private static async Task<IResult> InstallAsync(
        string id,
        [FromBody] ModelProviderInstallRequest? body,
        [FromServices] ITenantModelProviderInstallService installService,
        [FromServices] IRuntimeCatalog catalog,
        CancellationToken cancellationToken)
    {
        var provider = catalog.GetModelProvider(id);
        if (provider is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        ModelProviderInstallConfig? config = body is null
            ? null
            : new ModelProviderInstallConfig(
                Models: body.Models ?? Array.Empty<string>(),
                DefaultModel: body.DefaultModel,
                BaseUrl: body.BaseUrl);

        var install = await installService.InstallAsync(id, config, cancellationToken);
        var response = ToResponse(install, provider);
        return response is null
            ? Results.Problem(
                detail: "Provider was installed but could not be projected.",
                statusCode: StatusCodes.Status500InternalServerError)
            : Results.Ok(response);
    }

    private static async Task<IResult> UninstallAsync(
        string id,
        [FromServices] ITenantModelProviderInstallService installService,
        CancellationToken cancellationToken)
    {
        await installService.UninstallAsync(id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateConfigAsync(
        string id,
        [FromBody] ModelProviderInstallConfig config,
        [FromServices] ITenantModelProviderInstallService installService,
        [FromServices] IRuntimeCatalog catalog,
        CancellationToken cancellationToken)
    {
        var provider = catalog.GetModelProvider(id);
        if (provider is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var install = await installService.UpdateConfigAsync(id, config, cancellationToken);
            var response = ToResponse(install, provider);
            return response is null
                ? Results.Problem(
                    detail: "Provider config was updated but could not be projected.",
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

    private static async Task<IResult> GetCredentialHealthAsync(
        string id,
        [FromServices] IRuntimeCatalog catalog,
        [FromServices] ICredentialHealthStore credentialHealthStore,
        [FromQuery] string? secretName,
        CancellationToken cancellationToken)
    {
        if (catalog.GetModelProvider(id) is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var resolvedSecret = string.IsNullOrWhiteSpace(secretName) ? "default" : secretName;
        var row = await credentialHealthStore.GetAsync(
            CredentialHealthKind.AgentRuntime, id, resolvedSecret, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"No credential-health row recorded for model provider '{id}' / '{resolvedSecret}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new CredentialHealthResponse(
            SubjectId: row.SubjectId,
            SecretName: row.SecretName,
            Status: row.Status,
            LastError: row.LastError,
            LastChecked: row.LastChecked));
    }

    private static async Task<IResult> RefreshModelsAsync(
        string id,
        [FromBody] ModelProviderRefreshModelsRequest? body,
        [FromServices] IRuntimeCatalog catalog,
        [FromServices] IModelProviderAdapterRegistry adapterRegistry,
        [FromServices] ITenantModelProviderInstallService installService,
        CancellationToken cancellationToken)
    {
        var provider = catalog.GetModelProvider(id);
        if (provider is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var install = await installService.GetAsync(id, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var adapter = adapterRegistry.Get(provider.Adapter);
        if (adapter is null)
        {
            return Results.Problem(
                detail: $"No model-provider adapter registered for adapter id '{provider.Adapter}'.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var credential = body?.Credential;
        var fetch = await adapter.FetchLiveModelsAsync(provider, credential, cancellationToken);

        if (fetch.Status is not FetchLiveModelsStatus.Success)
        {
            var (statusCode, prefix) = fetch.Status switch
            {
                FetchLiveModelsStatus.InvalidCredential => (StatusCodes.Status401Unauthorized, "Credential rejected"),
                FetchLiveModelsStatus.Unsupported => (StatusCodes.Status502BadGateway, "Live catalogue not supported"),
                FetchLiveModelsStatus.NetworkError => (StatusCodes.Status502BadGateway, "Upstream fetch failed"),
                _ => (StatusCodes.Status502BadGateway, "Unknown fetch outcome"),
            };
            return Results.Problem(
                title: prefix,
                detail: fetch.ErrorMessage ?? prefix,
                statusCode: statusCode);
        }

        var liveIds = fetch.Models.Select(m => m.Id).Where(mid => !string.IsNullOrWhiteSpace(mid)).ToArray();
        var existingDefault = install.Config.DefaultModel;
        var preservedDefault = existingDefault is not null
            && liveIds.Any(mid => string.Equals(mid, existingDefault, StringComparison.OrdinalIgnoreCase));
        var nextDefault = preservedDefault
            ? existingDefault
            : (liveIds.Length > 0 ? liveIds[0] : null);

        var nextConfig = new ModelProviderInstallConfig(
            Models: liveIds,
            DefaultModel: nextDefault,
            BaseUrl: install.Config.BaseUrl);

        try
        {
            var updated = await installService.UpdateConfigAsync(id, nextConfig, cancellationToken);
            var response = ToResponse(updated, provider);
            return response is null
                ? Results.Problem(
                    detail: "Live-model catalogue was fetched but the install row could not be projected.",
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

    private static async Task<IResult> GetConfigAsync(
        string id,
        [FromServices] ITenantModelProviderInstallService installService,
        [FromServices] IRuntimeCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (catalog.GetModelProvider(id) is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }
        var install = await installService.GetAsync(id, cancellationToken);
        if (install is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not installed on the current tenant.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new ModelProviderConfigResponse(
            Id: install.ProviderId,
            Models: install.Config.Models,
            DefaultModel: install.Config.DefaultModel,
            BaseUrl: install.Config.BaseUrl));
    }

    private static async Task<IResult> ValidateCredentialAsync(
        string id,
        [FromBody] ModelProviderValidateCredentialRequest? body,
        [FromServices] IRuntimeCatalog catalog,
        [FromServices] IModelProviderAdapterRegistry adapterRegistry,
        [FromServices] ICredentialHealthStore credentialHealthStore,
        CancellationToken cancellationToken)
    {
        var provider = catalog.GetModelProvider(id);
        if (provider is null)
        {
            return Results.Problem(
                detail: $"Model provider '{id}' is not registered with the host.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var secretName = string.IsNullOrWhiteSpace(body?.SecretName)
            ? "default"
            : body.SecretName;

        // Providers that declare no credential schema (e.g. local Ollama)
        // have nothing to probe — surface that as a friendly Unknown payload
        // without persisting a credential-health row.
        if (provider.AuthMethods.Count == 0)
        {
            return Results.Ok(new ModelProviderValidateCredentialResponse(
                Ok: false,
                Status: CredentialHealthStatus.Unknown,
                Detail: $"Model provider '{provider.Id}' does not require credentials.",
                ValidatedAt: DateTimeOffset.UtcNow));
        }

        var adapter = adapterRegistry.Get(provider.Adapter);
        if (adapter is null)
        {
            return Results.Problem(
                detail: $"No model-provider adapter registered for adapter id '{provider.Adapter}'.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var credential = body?.Credential ?? string.Empty;

        // For now, validate by attempting a live-catalogue fetch — the
        // adapter's IsCredentialFormatAccepted is pre-flight only. The
        // adapter's FetchLiveModelsAsync surfaces InvalidCredential /
        // NetworkError / Unsupported / Success, which we map to the
        // credential-health states.
        var fetch = await adapter.FetchLiveModelsAsync(provider, credential, cancellationToken);
        var (ok, status, detail) = fetch.Status switch
        {
            FetchLiveModelsStatus.Success =>
                (true, CredentialHealthStatus.Valid, (string?)null),
            FetchLiveModelsStatus.InvalidCredential =>
                (false, CredentialHealthStatus.Invalid, fetch.ErrorMessage),
            FetchLiveModelsStatus.NetworkError =>
                (false, CredentialHealthStatus.Unknown, fetch.ErrorMessage),
            FetchLiveModelsStatus.Unsupported =>
                (false, CredentialHealthStatus.Unknown, fetch.ErrorMessage
                    ?? "Provider adapter does not support credential validation through the live-catalogue endpoint."),
            _ =>
                (false, CredentialHealthStatus.Unknown, fetch.ErrorMessage ?? "Unknown validation outcome."),
        };

        // NetworkError doesn't flip persistent state — same rule the legacy
        // path followed so a transient blip never moves a Valid row to
        // Unknown.
        if (fetch.Status != FetchLiveModelsStatus.NetworkError)
        {
            await credentialHealthStore.RecordAsync(
                CredentialHealthKind.AgentRuntime,
                provider.Id,
                secretName,
                status,
                lastError: detail,
                cancellationToken);
        }

        return Results.Ok(new ModelProviderValidateCredentialResponse(
            Ok: ok,
            Status: status,
            Detail: detail,
            ValidatedAt: DateTimeOffset.UtcNow));
    }

    private static InstalledModelProviderResponse? ToResponse(
        InstalledModelProvider install,
        ModelProvider? provider)
    {
        if (provider is null)
        {
            return null;
        }

        // ADR-0038 § "Credential identity": secret name is
        // {provider}-{authMethod-slug}. With multiple auth methods we
        // surface the first; the wizard handles the rest by edge.
        var primaryAuth = provider.AuthMethods.Count > 0 ? provider.AuthMethods[0] : (AuthMethod?)null;
        var credentialKind = primaryAuth switch
        {
            AuthMethod.Oauth => ModelProviderCredentialKind.OAuthToken,
            AuthMethod.ApiKey => ModelProviderCredentialKind.ApiKey,
            _ => ModelProviderCredentialKind.None,
        };
        var secretName = primaryAuth is AuthMethod method
            ? CredentialNaming.SecretNameFor(provider.Id, method)
            : string.Empty;

        return new InstalledModelProviderResponse(
            Id: install.ProviderId,
            DisplayName: provider.DisplayName,
            InstalledAt: install.InstalledAt,
            UpdatedAt: install.UpdatedAt,
            Models: install.Config.Models,
            DefaultModel: install.Config.DefaultModel,
            BaseUrl: install.Config.BaseUrl,
            CredentialKind: credentialKind,
            CredentialDisplayHint: null,
            CredentialSecretName: secretName);
    }
}