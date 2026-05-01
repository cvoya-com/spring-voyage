// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Net;
using System.Net.Http;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Secrets;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Octokit;

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
    private readonly IGitHubCollaboratorsClient _collaboratorsClient;
    private readonly GitHubAppConfigurationRequirement _credentialRequirement;
    private readonly IOAuthSessionStore _oauthSessionStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly IGitHubUserScopeResolver _userScopeResolver;
    private readonly ILogger<GitHubConnectorType> _logger;

    /// <summary>
    /// Creates a new <see cref="GitHubConnectorType"/>.
    /// </summary>
    public GitHubConnectorType(
        IUnitConnectorConfigStore configStore,
        IUnitConnectorRuntimeStore runtimeStore,
        IGitHubWebhookRegistrar webhookRegistrar,
        IGitHubInstallationsClient installationsClient,
        IGitHubCollaboratorsClient collaboratorsClient,
        IOptions<GitHubConnectorOptions> options,
        GitHubAppConfigurationRequirement credentialRequirement,
        IOAuthSessionStore oauthSessionStore,
        IServiceProvider serviceProvider,
        IGitHubUserScopeResolver userScopeResolver,
        ILoggerFactory loggerFactory)
    {
        _configStore = configStore;
        _runtimeStore = runtimeStore;
        _webhookRegistrar = webhookRegistrar;
        _installationsClient = installationsClient;
        _collaboratorsClient = collaboratorsClient;
        _options = options;
        _credentialRequirement = credentialRequirement;
        _oauthSessionStore = oauthSessionStore;
        _serviceProvider = serviceProvider;
        _userScopeResolver = userScopeResolver;
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

        // Aggregated repository list — one row per repo the App can see,
        // collapsed across every visible installation (#1133). Replaces
        // the v2 "type the owner / type the repo / pick the installation"
        // surface with a single dropdown the wizard splits client-side.
        // The installation id rides along on every row so the wizard can
        // post it back without a second resolver call.
        //
        // The optional `session_id` query parameter scopes the result to
        // only installations owned by the calling portal user's GitHub
        // identity (their login + organisations), preventing cross-tenant
        // repository leakage when the App is installed across multiple orgs
        // (#1505). When the parameter is absent the full unfiltered list is
        // returned — preserved for backward-compatibility with any CLI or
        // integration that calls this endpoint without a GitHub OAuth session.
        group.MapGet("/actions/list-repositories", ListRepositoriesAsync)
            .WithName("ListGitHubRepositories")
            .WithSummary("List repositories visible to the GitHub App, aggregated across installations, optionally scoped to the current user's identity via session_id")
            .WithTags("Connectors.GitHub")
            .Produces<GitHubRepositoryResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        // Collaborator list for a single repo (#1133). The wizard's
        // Reviewer dropdown re-fetches this whenever the repo selection
        // changes; the installation id is required so the connector can
        // mint the right token without doing a repo-to-installation
        // resolve on every call.
        group.MapGet("/actions/list-collaborators", ListCollaboratorsAsync)
            .WithName("ListGitHubCollaborators")
            .WithSummary("List collaborators on a repository visible to the GitHub App")
            .WithTags("Connectors.GitHub")
            .Produces<GitHubCollaboratorResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
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

    /// <inheritdoc />
    /// <remarks>
    /// The GitHub connector authenticates via App credentials
    /// (App ID + private key + installation id), not via a single bearer
    /// token, so the <paramref name="credential"/> parameter is currently
    /// ignored — the validation runs against the credentials already bound
    /// in <see cref="GitHubConnectorOptions"/>. The flow:
    /// <list type="number">
    ///   <item><description>If the App credentials are missing or malformed at startup (per <see cref="GitHubAppConfigurationRequirement"/>), return a result with <see cref="CredentialValidationStatus.Unknown"/> and the disabled reason.</description></item>
    ///   <item><description>Pick an installation id (the configured <see cref="GitHubConnectorOptions.InstallationId"/> when set; otherwise the first installation visible to the App).</description></item>
    ///   <item><description>Mint an installation access token and call <c>GET /installation/repositories</c> via <see cref="IGitHubInstallationsClient.ListInstallationRepositoriesAsync(long, CancellationToken)"/>.</description></item>
    ///   <item><description>Map the outcome: success → Valid; 401/403 → Invalid; transport / 5xx / DNS / TLS / timeout → NetworkError.</description></item>
    /// </list>
    /// </remarks>
    public virtual async Task<CredentialValidationResult?> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnectorEnabled)
        {
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: ConnectorDisabledReason,
                Status: CredentialValidationStatus.Unknown);
        }

        try
        {
            var installationId = _options.Value.InstallationId;
            if (installationId is null or 0)
            {
                var installations = await _installationsClient
                    .ListInstallationsAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (installations.Count == 0)
                {
                    // The App is configured but has no installations.
                    // We exchanged the App JWT successfully (otherwise the
                    // listing call would have thrown), so the credentials
                    // are valid even though there's nothing to enumerate.
                    return new CredentialValidationResult(
                        Valid: true,
                        ErrorMessage: null,
                        Status: CredentialValidationStatus.Valid);
                }

                installationId = installations[0].InstallationId;
            }

            // GET /installation/repositories — the canonical "is this
            // installation token actually accepted" probe.
            _ = await _installationsClient
                .ListInstallationRepositoriesAsync(installationId.Value, cancellationToken)
                .ConfigureAwait(false);

            return new CredentialValidationResult(
                Valid: true,
                ErrorMessage: null,
                Status: CredentialValidationStatus.Valid);
        }
        catch (AuthorizationException ex)
        {
            _logger.LogInformation(ex,
                "GitHub App credential validation rejected by GitHub (status {Status}).",
                ex.StatusCode);
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: ex.Message,
                Status: CredentialValidationStatus.Invalid);
        }
        catch (ApiException ex) when (
            ex.StatusCode == HttpStatusCode.Unauthorized
            || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogInformation(ex,
                "GitHub App credential validation rejected by GitHub (status {Status}).",
                ex.StatusCode);
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: ex.Message,
                Status: CredentialValidationStatus.Invalid);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "GitHub App credential validation could not reach GitHub.");
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: ex.Message,
                Status: CredentialValidationStatus.NetworkError);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Request-side timeout (Octokit / HttpClient) — caller's token
            // wasn't tripped, so this is a transport-level failure rather
            // than a cooperative cancel.
            _logger.LogWarning(ex,
                "GitHub App credential validation timed out reaching GitHub.");
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: ex.Message,
                Status: CredentialValidationStatus.NetworkError);
        }
        catch (ApiException ex)
        {
            // Any other API error (5xx, rate-limit, etc.) — credential
            // validity is unknown; surface as NetworkError so the caller
            // can retry.
            _logger.LogWarning(ex,
                "GitHub App credential validation failed with API error (status {Status}).",
                ex.StatusCode);
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: ex.Message,
                Status: CredentialValidationStatus.NetworkError);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The GitHub connector talks to <c>api.github.com</c> over outbound
    /// HTTPS only — there is no host-side binary or side-car to verify.
    /// We return a passing result rather than <c>null</c> so the install /
    /// wizard surface renders "checked, OK" instead of "skipped" for the
    /// connector that most operators care about.
    /// </remarks>
    public virtual Task<ContainerBaselineCheckResult?> VerifyContainerBaselineAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<ContainerBaselineCheckResult?>(
            new ContainerBaselineCheckResult(Passed: true, Errors: Array.Empty<string>()));

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
        // Reviewer is optional. Treat whitespace as "no default reviewer"
        // so the wizard's "(none)" sentinel ("") doesn't accidentally
        // persist an empty login that the PR-review skill would later
        // try to assign.
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer)
            ? null
            : request.Reviewer.Trim();
        var config = new UnitGitHubConfig(
            request.Owner, request.Repo, request.AppInstallationId, events, reviewer);

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

    private async Task<IResult> ListRepositoriesAsync(
        [FromQuery(Name = "session_id")] string? sessionId,
        CancellationToken cancellationToken)
    {
        // Same disabled short-circuit as list-installations — without
        // valid App credentials we can't mint installation tokens, so
        // every per-installation /installation/repositories call would
        // fail with a JWT-sign error. Surface the structured "disabled"
        // payload the portal already renders cleanly (#609).
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
            // #1505: Resolve the caller's GitHub identity when a GitHub
            // OAuth session id is supplied. The session was established by
            // the portal's OAuth flow (POST /oauth/authorize → GET
            // /oauth/callback) and ties the portal user to their GitHub
            // login + org memberships. We filter the App's installations
            // to only those whose account login is in that set, preventing
            // cross-tenant repository leakage when the App is installed on
            // multiple organisations belonging to different tenants.
            //
            // When no session_id is supplied we fall back to the
            // unfiltered list (backward-compatible for CLI and integration
            // callers that do not carry an OAuth session). A warning is
            // logged so operators can audit unauthenticated calls.
            IReadOnlySet<string>? userScope = null;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var session = await _oauthSessionStore.GetAsync(sessionId, cancellationToken);
                if (session is not null)
                {
                    // Resolve ISecretStore lazily — it may have heavyweight
                    // activation requirements (e.g. Dapr state store needing
                    // an AES key) that should not block the OpenAPI generation
                    // process or cold-path startup.
                    var secretStore = _serviceProvider.GetRequiredService<ISecretStore>();
                    var accessToken = await secretStore.ReadAsync(
                        session.AccessTokenStoreKey, cancellationToken);
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        userScope = await _userScopeResolver.ResolveAsync(
                            accessToken, cancellationToken);
                        _logger.LogInformation(
                            "list-repositories: user scope resolved for session {SessionId} " +
                            "(login={Login}, accounts={Count})",
                            sessionId, session.Login, userScope.Count);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "list-repositories: session {SessionId} found but access token is missing " +
                            "from the secret store; returning unfiltered list",
                            sessionId);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "list-repositories: session_id '{SessionId}' not found; " +
                        "returning unfiltered list",
                        sessionId);
                }
            }
            else
            {
                _logger.LogInformation(
                    "list-repositories: no session_id supplied; " +
                    "returning all installations (unfiltered)");
            }

            var installations = await _installationsClient
                .ListInstallationsAsync(cancellationToken);

            // When we have a user scope, retain only installations whose
            // account login is in { user-login, user-orgs }. GitHub
            // logins are case-preserving but case-insensitive, so use
            // the case-insensitive comparer the HashSet was built with.
            var visibleInstallations = userScope is not null
                ? installations
                    .Where(i => userScope.Contains(i.Account))
                    .ToList()
                : installations;

            if (userScope is not null)
            {
                _logger.LogInformation(
                    "list-repositories: filtered {Total} installation(s) down to {Visible} " +
                    "matching the caller's GitHub scope",
                    installations.Count, visibleInstallations.Count);
            }

            // Aggregate across installations so the wizard can present a
            // single repository dropdown (#1133). The per-installation
            // call goes through the existing
            // ListInstallationRepositoriesAsync which mints an installation
            // token and pages through GET /installation/repositories.
            // A failure on one installation MUST NOT poison the list —
            // log it and keep the other installations' rows so the wizard
            // still has something to render.
            var aggregated = new List<GitHubRepositoryResponse>();
            foreach (var installation in visibleInstallations)
            {
                IReadOnlyList<GitHubInstallationRepository> repos;
                try
                {
                    repos = await _installationsClient
                        .ListInstallationRepositoriesAsync(installation.InstallationId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to list repositories for installation {InstallationId} ({Account}); skipping",
                        installation.InstallationId, installation.Account);
                    continue;
                }

                foreach (var repo in repos)
                {
                    aggregated.Add(new GitHubRepositoryResponse(
                        installation.InstallationId,
                        repo.RepositoryId,
                        repo.Owner,
                        repo.Name,
                        repo.FullName,
                        repo.Private));
                }
            }

            // Stable order — sort by full name so the wizard's dropdown
            // doesn't shuffle between renders. GitHub itself returns the
            // list in install-time order, which is meaningless to a user
            // browsing a long catalogue.
            var ordered = aggregated
                .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(ordered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to aggregate GitHub repositories");
            return Results.Problem(
                title: "Failed to list GitHub repositories",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<IResult> ListCollaboratorsAsync(
        [FromQuery(Name = "installation_id")] long installationId,
        [FromQuery] string owner,
        [FromQuery] string repo,
        CancellationToken cancellationToken)
    {
        if (installationId <= 0
            || string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(repo))
        {
            return Results.Problem(
                title: "Missing required parameters",
                detail: "installation_id, owner, and repo are all required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

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
            var collaborators = await _collaboratorsClient
                .ListCollaboratorsAsync(installationId, owner, repo, cancellationToken);
            var response = collaborators
                .Select(c => new GitHubCollaboratorResponse(c.Login, c.AvatarUrl))
                .ToArray();
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to list collaborators for {Owner}/{Repo} (installation {InstallationId})",
                owner, repo, installationId);
            return Results.Problem(
                title: "Failed to list GitHub collaborators",
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
    {
        // #1146: the persisted binding distinguishes "operator picked an
        // explicit set" (Events has at least one entry) from "use the
        // connector defaults" (Events is null or empty — same sentinel
        // PutConfigAsync collapses to). Surfacing the distinction
        // verbatim lets the portal's connector tab round-trip the
        // wizard's "Connector defaults" toggle without resorting to a
        // lossy "events == DEFAULT_EVENTS" client heuristic.
        var eventsAreDefault = config.Events is not { Count: > 0 };
        return new UnitGitHubConfigResponse(
            unitId,
            config.Owner,
            config.Repo,
            config.AppInstallationId,
            eventsAreDefault ? DefaultEvents : config.Events!,
            config.Reviewer,
            eventsAreDefault);
    }

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
            },
            "reviewer": {
              "type": ["string", "null"],
              "description": "Default GitHub login (no leading @) requested as the reviewer on pull requests opened by this unit."
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