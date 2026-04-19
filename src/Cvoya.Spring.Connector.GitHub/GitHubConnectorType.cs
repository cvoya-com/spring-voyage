// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Configuration;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// GitHub concrete implementation of <see cref="IConnectorType"/>. Registers
/// the GitHub typed per-unit config endpoints and connector-scoped actions
/// (<c>installations</c>, <c>install-url</c>) under the host-provided
/// <c>/api/v1/connectors/github</c> group, and handles the GitHub-specific
/// webhook lifecycle hooks on unit start/stop.
/// </summary>
public class GitHubConnectorType : IConnectorType
{
    /// <summary>
    /// The stable identity persisted on every unit binding. Changing this
    /// value invalidates existing bindings — never change it in place.
    /// </summary>
    public static readonly Guid GitHubTypeId =
        new("6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01");

    // Default webhook events a newly-bound unit subscribes to. Mirrors the
    // set the webhook registrar was using pre-generic-refactor so behaviour
    // is preserved for units created without an explicit Events list.
    private static readonly IReadOnlyList<string> DefaultEvents = new[]
    {
        "issues",
        "pull_request",
        "issue_comment",
    };

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IGitHubWebhookRegistrar _webhookRegistrar;
    private readonly IUnitConnectorRuntimeStore _runtimeStore;
    private readonly IOptions<GitHubConnectorOptions> _options;
    private readonly IGitHubInstallationsClient _installationsClient;
    private readonly GitHubAppConfigurationRequirement _credentialRequirement;
    private readonly ILogger<GitHubConnectorType> _logger;

    /// <summary>
    /// Creates a new <see cref="GitHubConnectorType"/>.
    /// </summary>
    public GitHubConnectorType(
        IUnitConnectorConfigStore configStore,
        IUnitConnectorRuntimeStore runtimeStore,
        IGitHubWebhookRegistrar webhookRegistrar,
        IGitHubInstallationsClient installationsClient,
        IOptions<GitHubConnectorOptions> options,
        GitHubAppConfigurationRequirement credentialRequirement,
        ILoggerFactory loggerFactory)
    {
        _configStore = configStore;
        _runtimeStore = runtimeStore;
        _webhookRegistrar = webhookRegistrar;
        _installationsClient = installationsClient;
        _options = options;
        _credentialRequirement = credentialRequirement;
        _logger = loggerFactory.CreateLogger<GitHubConnectorType>();
    }

    /// <summary>
    /// Returns <c>true</c> when the connector has usable App credentials.
    /// Reads the current <see cref="IConfigurationRequirement"/> status so the
    /// hot-path short-circuit is driven by the same signal the
    /// <c>/system/configuration</c> report surfaces.
    /// </summary>
    private bool IsConnectorEnabled =>
        _credentialRequirement.GetCurrentStatus().Status == ConfigurationStatus.Met;

    /// <summary>
    /// Disabled reason reported to endpoint callers when
    /// <see cref="IsConnectorEnabled"/> is <c>false</c>. Matches the structure
    /// the portal and CLI render — a short human sentence the operator can
    /// act on.
    /// </summary>
    private string? ConnectorDisabledReason =>
        _credentialRequirement.GetCurrentStatus().Reason;

    /// <inheritdoc />
    public Guid TypeId => GitHubTypeId;

    /// <inheritdoc />
    public string Slug => "github";

    /// <inheritdoc />
    public string DisplayName => "GitHub";

    /// <inheritdoc />
    public string Description => "Connect a unit to a GitHub repository so the platform relays issue and pull-request events as messages.";

    /// <inheritdoc />
    public Type ConfigType => typeof(UnitGitHubConfig);

    /// <inheritdoc />
    public void MapRoutes(IEndpointRouteBuilder group)
    {
        // Per-unit typed config: GET/PUT under {unitId}/config. The PUT
        // atomically binds the connector type AND writes the typed config.
        group.MapGet("/units/{unitId}/config", GetConfigAsync)
            .WithName("GetUnitGitHubConnectorConfig")
            .WithSummary("Get the GitHub connector config bound to a unit")
            .WithTags("Connectors.GitHub")
            .Produces<UnitGitHubConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/units/{unitId}/config", PutConfigAsync)
            .WithName("PutUnitGitHubConnectorConfig")
            .WithSummary("Bind a unit to GitHub and upsert its per-unit config")
            .WithTags("Connectors.GitHub")
            .Accepts<UnitGitHubConfigRequest>("application/json")
            .Produces<UnitGitHubConfigResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Connector-scoped actions — not tied to a single unit.
        group.MapGet("/actions/list-installations", ListInstallationsAsync)
            .WithName("ListGitHubInstallations")
            .WithSummary("List GitHub App installations visible to the configured App")
            .WithTags("Connectors.GitHub")
            .Produces<GitHubInstallationResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/actions/install-url", GetInstallUrlAsync)
            .WithName("GetGitHubInstallUrl")
            .WithSummary("Get the GitHub App install URL the wizard should redirect the user through")
            .WithTags("Connectors.GitHub")
            .Produces<GitHubInstallUrlResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        // Config-schema: returns a hand-authored JSON Schema matching the
        // UnitGitHubConfigRequest body. Returned as JsonElement so the OpenAPI
        // generator surfaces a single concrete response type (no oneOf).
        group.MapGet("/config-schema", GetConfigSchemaEndpointAsync)
            .WithName("GetGitHubConnectorConfigSchema")
            .WithSummary("Get the JSON Schema describing the GitHub connector config body")
            .WithTags("Connectors.GitHub")
            .Produces<JsonElement>(StatusCodes.Status200OK);

        // OAuth flow endpoints — authorize / callback / revoke / session.
        // Owned by GitHubOAuthEndpoints so this class stays focused on the
        // App-installation surface.
        group.MapOAuthEndpoints();
    }

    /// <inheritdoc />
    public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<JsonElement?>(BuildConfigSchema());

    /// <inheritdoc />
    public async Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var config = await LoadConfigAsync(unitId, cancellationToken);
        if (config is null)
        {
            return;
        }

        try
        {
            var hookId = await _webhookRegistrar.RegisterAsync(
                config.Owner, config.Repo, cancellationToken);

            // Persist the hook id so OnUnitStoppingAsync can tear it down.
            var runtime = JsonSerializer.SerializeToElement(
                new GitHubConnectorRuntime(hookId), ConfigJson);
            await _runtimeStore.SetAsync(unitId, runtime, cancellationToken);

            _logger.LogInformation(
                "Registered GitHub webhook {HookId} for unit {UnitId} on {Owner}/{Repo}",
                hookId, unitId, config.Owner, config.Repo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to register GitHub webhook for unit {UnitId} on {Owner}/{Repo}. Proceeding to Running; events will not flow until the hook is created manually.",
                unitId, config.Owner, config.Repo);
        }
    }

    /// <inheritdoc />
    public async Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var config = await LoadConfigAsync(unitId, cancellationToken);
        if (config is null)
        {
            return;
        }

        var runtimeElement = await _runtimeStore.GetAsync(unitId, cancellationToken);
        if (runtimeElement is null)
        {
            return;
        }

        GitHubConnectorRuntime? runtime;
        try
        {
            runtime = runtimeElement.Value.Deserialize<GitHubConnectorRuntime>(ConfigJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Unit {UnitId} connector runtime metadata was not GitHub-shaped; skipping teardown.",
                unitId);
            return;
        }

        if (runtime is null || runtime.HookId <= 0)
        {
            return;
        }

        try
        {
            await _webhookRegistrar.UnregisterAsync(
                config.Owner, config.Repo, runtime.HookId, cancellationToken);
            await _runtimeStore.ClearAsync(unitId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to delete GitHub webhook {HookId} for unit {UnitId} on {Owner}/{Repo}. Continuing teardown; the hook id remains persisted so operators can retry.",
                runtime.HookId, unitId, config.Owner, config.Repo);
        }
    }

    private async Task<UnitGitHubConfig?> LoadConfigAsync(string unitId, CancellationToken ct)
    {
        var binding = await _configStore.GetAsync(unitId, ct);
        if (binding is null || binding.TypeId != GitHubTypeId)
        {
            return null;
        }

        try
        {
            return binding.Config.Deserialize<UnitGitHubConfig>(ConfigJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Unit {UnitId} is bound to GitHub but the stored config could not be deserialized.",
                unitId);
            return null;
        }
    }

    private async Task<IResult> GetConfigAsync(
        string unitId, CancellationToken cancellationToken)
    {
        var binding = await _configStore.GetAsync(unitId, cancellationToken);
        if (binding is null || binding.TypeId != GitHubTypeId)
        {
            return Results.Problem(
                detail: $"Unit '{unitId}' is not bound to the GitHub connector.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var config = binding.Config.Deserialize<UnitGitHubConfig>(ConfigJson);
        if (config is null)
        {
            return Results.Problem(
                detail: $"Stored config for unit '{unitId}' is not GitHub-shaped.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToResponse(unitId, config));
    }

    private async Task<IResult> PutConfigAsync(
        string unitId,
        [FromBody] UnitGitHubConfigRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Owner) || string.IsNullOrWhiteSpace(request.Repo))
        {
            return Results.Problem(
                detail: "Both 'owner' and 'repo' are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var events = request.Events is { Count: > 0 } ? request.Events : null;
        var config = new UnitGitHubConfig(
            request.Owner, request.Repo, request.AppInstallationId, events);

        var payload = JsonSerializer.SerializeToElement(config, ConfigJson);
        await _configStore.SetAsync(unitId, GitHubTypeId, payload, cancellationToken);

        return Results.Ok(ToResponse(unitId, config));
    }

    private async Task<IResult> ListInstallationsAsync(CancellationToken cancellationToken)
    {
        // Short-circuit when the connector never received usable App
        // credentials at startup (#609). Returns a structured 404 the
        // portal (PR #610) and CLI render cleanly as "GitHub App not
        // configured" instead of a 502 from a downstream JWT sign that
        // is guaranteed to fail.
        if (!IsConnectorEnabled)
        {
            var reason = ConnectorDisabledReason;
            return Results.Problem(
                title: "GitHub connector is not configured",
                detail: reason,
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>
                {
                    ["disabled"] = true,
                    ["reason"] = reason,
                });
        }

        try
        {
            var installations = await _installationsClient.ListInstallationsAsync(cancellationToken);
            var response = installations
                .Select(i => new GitHubInstallationResponse(
                    i.InstallationId, i.Account, i.AccountType, i.RepoSelection))
                .ToArray();
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list GitHub App installations");
            return Results.Problem(
                title: "Failed to list GitHub App installations",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private IResult GetInstallUrlAsync()
    {
        // Same disabled short-circuit as list-installations — the install
        // URL only makes sense when there is a configured App slug; if the
        // credentials aren't configured the slug usually isn't either, and
        // surfacing the disabled state uniformly keeps both surfaces
        // (portal + CLI) happy (#609).
        if (!IsConnectorEnabled)
        {
            var reason = ConnectorDisabledReason;
            return Results.Problem(
                title: "GitHub connector is not configured",
                detail: reason,
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>
                {
                    ["disabled"] = true,
                    ["reason"] = reason,
                });
        }

        var slug = _options.Value.AppSlug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Results.Problem(
                title: "GitHub App slug is not configured",
                detail: "Configure 'GitHub:AppSlug' so the platform can build the install URL.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var url = $"https://github.com/apps/{Uri.EscapeDataString(slug)}/installations/new";
        return Results.Ok(new GitHubInstallUrlResponse(url));
    }

    private static IResult GetConfigSchemaEndpointAsync()
    {
        return Results.Ok(BuildConfigSchema());
    }

    private UnitGitHubConfigResponse ToResponse(string unitId, UnitGitHubConfig config)
        => new(
            unitId,
            config.Owner,
            config.Repo,
            config.AppInstallationId,
            config.Events is { Count: > 0 } ? config.Events : DefaultEvents);

    // Hand-authored schema — deriving from C# via reflection would be cleaner
    // but .NET 10's OpenAPI generator doesn't expose the per-component schema
    // as JSON at runtime, and this payload is tiny. If it ever drifts from
    // UnitGitHubConfigRequest the contract tests will catch the mismatch.
    private static JsonElement BuildConfigSchema()
    {
        const string schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "UnitGitHubConfigRequest",
          "type": "object",
          "required": ["owner", "repo"],
          "properties": {
            "owner": { "type": "string", "description": "The repository owner (user or organization login)." },
            "repo": { "type": "string", "description": "The repository name." },
            "appInstallationId": { "type": ["integer", "null"], "description": "The GitHub App installation id powering the binding." },
            "events": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Webhook events to subscribe to. Null falls back to the connector's default set."
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }
}

/// <summary>
/// Serializable runtime metadata the GitHub connector persists per-unit —
/// currently just the webhook id that /start created and /stop needs.
/// </summary>
/// <param name="HookId">The GitHub webhook id returned at registration time.</param>
internal record GitHubConnectorRuntime(long HookId);