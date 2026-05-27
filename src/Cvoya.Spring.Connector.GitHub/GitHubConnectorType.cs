// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Net;
using System.Net.Http;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
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
/// <c>/api/v1/connectors/github</c> group.
///
/// <para>
/// Issue #2456 — the GitHub connector relies on <strong>App-level
/// delivery</strong>: the operator installs the GitHub App on the repos
/// they want SV to see, GitHub delivers every event to the App-wide
/// webhook URL, and the platform routes each delivery to the bound unit
/// (or drops it silently when no unit is bound to that <c>(installation_id,
/// owner, repo)</c>). There is no per-repo hook creation on unit start /
/// stop — and therefore no <c>OnUnitStartingAsync</c> /
/// <c>OnUnitStoppingAsync</c> here. The principle is locked in
/// <c>docs/decisions/0045-connector-domain-agnostic-platform.md</c>:
/// connectors facilitate flow; they do not replicate upstream
/// subscription configs.
/// </para>
/// </summary>
public class GitHubConnectorType : IConnectorType
{
    /// <summary>
    /// The stable identity persisted on every unit binding. Changing this
    /// value invalidates existing bindings — never change it in place.
    /// </summary>
    public static readonly Guid GitHubTypeId =
        new("6a1e0c1a-3a7b-4a12-8a2f-0a71e1b2fb01");

    // Default webhook events a newly-bound unit's UI presents when the
    // operator leaves the per-binding Events list empty. The platform no
    // longer installs per-repo hooks (#2456); the GitHub App's own
    // subscription scope determines what GitHub delivers. This list is
    // kept only so the ToResponse path can echo "binding accepts the
    // default set" back to the wizard.
    private static readonly IReadOnlyList<string> DefaultEvents = new[]
    {
        "issues",
        "pull_request",
        "issue_comment",
    };

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IOptions<GitHubConnectorOptions> _options;
    private readonly IGitHubInstallationsClient _installationsClient;
    private readonly IGitHubCollaboratorsClient _collaboratorsClient;
    private readonly GitHubAppConfigurationRequirement _credentialRequirement;
    private readonly IOAuthSessionStore _oauthSessionStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GitHubConnectorType> _logger;

    /// <summary>
    /// Creates a new <see cref="GitHubConnectorType"/>.
    /// </summary>
    public GitHubConnectorType(
        IUnitConnectorConfigStore configStore,
        IGitHubInstallationsClient installationsClient,
        IGitHubCollaboratorsClient collaboratorsClient,
        IOptions<GitHubConnectorOptions> options,
        GitHubAppConfigurationRequirement credentialRequirement,
        IOAuthSessionStore oauthSessionStore,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        _configStore = configStore;
        _installationsClient = installationsClient;
        _collaboratorsClient = collaboratorsClient;
        _options = options;
        _credentialRequirement = credentialRequirement;
        _oauthSessionStore = oauthSessionStore;
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<GitHubConnectorType>();
    }

    // Tries to mint an authorize URL the portal can deep-link to from the
    // missing-OAuth panel. Returns null when GitHub:OAuth is not configured
    // or the OAuth service is otherwise unreachable — the portal then
    // renders a "ask your operator to configure GitHub OAuth" message
    // instead of a half-broken button. Resolved through the service
    // provider rather than constructor-injected so the connector keeps
    // working when the OAuth wiring is absent (the OAuth service throws
    // InvalidOperationException at the call site rather than at DI time).
    private async Task<(string? Url, string? State)> TryBuildAuthorizeUrlAsync(
        CancellationToken cancellationToken)
    {
        var oauthService = _serviceProvider.GetService(typeof(IGitHubOAuthService))
            as IGitHubOAuthService;
        if (oauthService is null)
        {
            return (null, null);
        }

        try
        {
            var result = await oauthService.BeginAuthorizationAsync(
                scopesOverride: null,
                clientState: null,
                initiation: null,
                cancellationToken);
            return (result.AuthorizeUrl, result.State);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation(ex,
                "list-repositories: GitHub OAuth not configured; " +
                "missing-OAuth response will omit the authorize URL");
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "list-repositories: failed to mint authorize URL; " +
                "missing-OAuth response will omit it");
            return (null, null);
        }
    }

    private async Task<IResult> MissingOAuthResultAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var (url, state) = await TryBuildAuthorizeUrlAsync(cancellationToken);
        return Results.Json(
            new GitHubMissingOAuthResponse(
                MissingOAuth: true,
                Reason: reason,
                AuthorizeUrl: url,
                State: state),
            statusCode: StatusCodes.Status401Unauthorized);
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
    /// <remarks>
    /// GitHub bindings are per-unit: each unit pins one repository plus its
    /// auth context. The per-tenant scope introduced in ADR-0061 §1 is for
    /// workspace-shaped connectors (Slack, calendar) — GitHub stays
    /// <see cref="BindingScope.Unit"/>.
    /// </remarks>
    public BindingScope BindingScope => BindingScope.Unit;

    /// <inheritdoc />
    public string DisplayName => "GitHub";

    /// <inheritdoc />
    public string Description => "Connect a unit to a GitHub repository so the platform relays issue and pull-request events as messages.";

    /// <inheritdoc />
    public Type ConfigType => typeof(UnitGitHubConfig);

    /// <inheritdoc />
    /// <remarks>
    /// ADR-0047 §4: GitHub contributes a strictly display-identity user-config
    /// schema. The CLR shape is <see cref="GitHubUserConfig"/>; no PAT, no
    /// installation override, no auth fields belong here.
    /// </remarks>
    public Type? UserConfigType => typeof(GitHubUserConfig);

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
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

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
        // #1663: the endpoint requires a `session_id` query parameter
        // tied to the caller's GitHub OAuth session, and the result is
        // intersected with both the installations the user's identity
        // can reach AND the per-repo permissions on the user's OAuth
        // token. A session-less call returns a structured 401 with
        // `missingOAuth=true` — the portal renders a "Link your GitHub
        // account" panel rather than a leaky installation list.
        group.MapGet("/actions/list-repositories", ListRepositoriesAsync)
            .WithName("ListGitHubRepositories")
            .WithSummary("List repositories the calling user can access in the GitHub App's installations; requires a GitHub OAuth session_id (fail-closed, #1663)")
            .WithTags("Connectors.GitHub")
            .Produces<GitHubRepositoryResponse[]>(StatusCodes.Status200OK)
            .Produces<GitHubMissingOAuthResponse>(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
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

        // User-config-schema: returns a hand-authored JSON Schema for the
        // per-TenantUser display-identity surface (ADR-0047 §4). Mirrors the
        // /config-schema route's shape so the portal and CLI consume both
        // surfaces uniformly. Returned as JsonElement for the same reason as
        // /config-schema — a single concrete response type rather than a oneOf.
        group.MapGet("/user-config-schema", GetUserConfigSchemaEndpointAsync)
            .WithName("GetGitHubConnectorUserConfigSchema")
            .WithSummary("Get the JSON Schema describing the GitHub connector per-TenantUser display-identity body")
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
    /// <remarks>
    /// ADR-0047 §4: a strictly display-identity schema —
    /// <c>{ username (required), display_handle (optional) }</c>. No PAT, no
    /// installation override; outbound credentials live on the unit binding
    /// row per ADR-0047 §11 and are described by
    /// <see cref="GetConfigSchemaAsync"/>.
    /// </remarks>
    public Task<JsonElement?> GetUserConfigSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<JsonElement?>(BuildUserConfigSchema());

    /// <inheritdoc />
    /// <remarks>
    /// Issue #2456 — no-op. The GitHub connector no longer creates
    /// per-repo webhook hooks on unit start; App-level delivery covers
    /// the entire inbound surface, and the GitHub App's installation
    /// scope is owned by the operator on github.com (not by the
    /// platform).
    /// </remarks>
    public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Issue #2456 — no-op. There is no per-repo hook to tear down on
    /// unit stop; uninstalling the GitHub App or removing the bound
    /// repo from the App's installation scope is operator-owned on
    /// github.com.
    /// </remarks>
    public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

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
        catch (GitHubClockSkewException ex)
        {
            // #2595: GitHub rejected the App JWT with "Bad credentials", but the
            // real cause is a skewed container clock — the credentials are fine.
            // Report NetworkError (the retriable, not-a-credential-problem bucket)
            // with the actionable message so the operator resyncs the clock
            // rather than re-checking the App ID / private key.
            _logger.LogWarning(ex,
                "GitHub App credential validation failed due to container clock skew.");
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: ex.Message,
                Status: CredentialValidationStatus.NetworkError);
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

    // LoadConfigAsync removed in #2456 — it was the helper the now-deleted
    // OnUnitStartingAsync / OnUnitStoppingAsync hooks used to fetch the
    // binding's typed config. Endpoint paths read the binding directly
    // through _configStore.

    // #1748: store calls below pass the unit's actor-Guid form because
    // the default UnitActorConnectorConfigStore routes through Address.For
    // internally. Resolution is best-effort — when no directory is wired,
    // when the route value isn't a Guid (test fixtures and other harnesses
    // that key by opaque string), or when the directory has no matching
    // entry, we fall through to the route value so the legacy contract
    // (store sees whatever the caller supplied) is preserved.
    private async Task<string> ResolveUnitActorIdAsync(string unitId, CancellationToken ct)
    {
        if (_serviceProvider.GetService(typeof(IDirectoryService)) is not IDirectoryService directory)
        {
            return unitId;
        }
        if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out _))
        {
            return unitId;
        }
        var entry = await directory.ResolveAsync(Address.For("unit", unitId), ct);
        return entry is null
            ? unitId
            : Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
    }

    private async Task<IResult> GetConfigAsync(
        string unitId, CancellationToken cancellationToken)
    {
        var actorId = await ResolveUnitActorIdAsync(unitId, cancellationToken);
        var binding = await _configStore.GetAsync(actorId, cancellationToken);
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
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Repo))
        {
            return Results.Problem(
                detail: "'repo' is required and must be in 'owner/repo' qualified form.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // ADR-0047 §11: Repo must be the qualified `owner/repo` form. The
        // wizard / CLI reject unqualified inputs before they reach the
        // server, but the server defends in depth — a one-segment value
        // would land a half-name on the binding row and make every
        // downstream Octokit call malformed.
        if (!request.Repo.Contains('/'))
        {
            return Results.Problem(
                detail: "'repo' must be in qualified 'owner/repo' form (ADR-0047 §11). " +
                        $"Got '{request.Repo}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // ADR-0047 §11 exactly-one-of gate. The binding-create-time check
        // here is the structural guarantee Phase D / §6 relies on at use
        // time — without this the connector's outbound auth path would
        // need a runtime ambiguity check.
        var authProblem = GitHubBindingAuthProblems.ValidateOrProblem(
            request.AppInstallationId, request.PatSecretName);
        if (authProblem is not null)
        {
            return authProblem;
        }

        // ADR-0047 §10 — reject cross-tenant collisions structurally.
        // Once installation_id is out of the routing fabric, the inbound
        // webhook payload for an (owner, repo) carries no tenant signal,
        // so two tenants both claiming the same repo cannot be
        // disambiguated. The probe walks every other tenant's GitHub
        // bindings before the row is inserted so the rejection is
        // atomic relative to the create.
        var probe = _serviceProvider.GetService(typeof(IConnectorBindingCrossTenantProbe))
            as IConnectorBindingCrossTenantProbe;
        if (probe is not null
            && await probe.HasCrossTenantBindingAsync(GitHubTypeId, request.Repo, cancellationToken)
                .ConfigureAwait(false))
        {
            return GitHubBindingAuthProblems.CrossTenantConflict(request.Repo);
        }

        var actorId = await ResolveUnitActorIdAsync(unitId, cancellationToken);

        var events = request.Events is { Count: > 0 } ? request.Events : null;
        // Reviewer is optional. Treat whitespace as "no default reviewer"
        // so the wizard's "(none)" sentinel ("") doesn't accidentally
        // persist an empty login that the PR-review skill would later
        // try to assign.
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer)
            ? null
            : request.Reviewer.Trim();

        // Trim the PAT secret name. The auth gate above already rejected
        // whitespace-only values; this just normalises a clean string for
        // downstream secret-store reads.
        var patSecretName = string.IsNullOrWhiteSpace(request.PatSecretName)
            ? null
            : request.PatSecretName.Trim();

        var config = new UnitGitHubConfig(
            request.Repo,
            request.AppInstallationId,
            patSecretName,
            events,
            reviewer,
            request.AddOnAssign,
            request.RemoveOnAssign,
            request.IncludeLabels,
            request.ExcludeLabels,
            request.IncludeAuthors,
            request.IncludePaths);

        var payload = JsonSerializer.SerializeToElement(config, ConfigJson);
        await _configStore.SetAsync(actorId, GitHubTypeId, payload, cancellationToken);

        // ADR-0047 §2 / §13: per-`TenantUser` connector-identity capture
        // moves to the OAuth flow (Phase F). The pre-ADR-0047 auto-seed of
        // the operator's GitHub identity from the binding's reviewer field
        // is dropped — that was semantically wrong (reviewer is some other
        // GitHub user, not the caller) and only worked in the
        // operator-is-reviewer OSS case. Phase F wires the OAuth user-info
        // path that populates `TenantUserConnectorIdentity.username` from
        // the GitHub API instead.

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

        // #1663: fail closed when no usable user OAuth session is in play.
        // The pre-1663 contract fell back to the App-installation list
        // when no session_id was supplied, which leaked every repo the
        // App could see — including ones the calling portal user has no
        // access to in their own GitHub identity. The fix:
        //
        //   * No session_id query param            → 401 missingOAuth
        //   * session_id present but unknown        → 401 missingOAuth
        //   * Session known but no token in store   → 401 missingOAuth
        //   * Session + token                        → user-scoped intersect (existing happy path)
        //
        // The 401 body carries an `authorizeUrl` minted from
        // IGitHubOAuthService.BeginAuthorizationAsync (when GitHub:OAuth
        // is configured) so the portal can tell configured deployments
        // apart from ones that still need OAuth App setup. The browser UI
        // starts its own authorize request with portal callback state when
        // the operator clicks "Link GitHub account".
        //
        // No CLI / integration caller drives this endpoint today (the
        // CLI lets operators set owner/repo by hand on the create-unit
        // command), so the fail-closed move is a contract tightening
        // rather than a backwards-compat breakage.
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogInformation(
                "list-repositories: no session_id supplied; returning 401 missingOAuth (#1663)");
            return await MissingOAuthResultAsync(
                "No GitHub OAuth session was supplied. The portal must " +
                "link the operator's GitHub account before listing repositories.",
                cancellationToken);
        }

        var session = await _oauthSessionStore.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning(
                "list-repositories: session_id '{SessionId}' not found; returning 401 missingOAuth (#1663)",
                sessionId);
            return await MissingOAuthResultAsync(
                "The GitHub OAuth session is unknown or has expired. " +
                "Link the operator's GitHub account again to refresh it.",
                cancellationToken);
        }

        // Resolve ISecretStore lazily — it may have heavyweight activation
        // requirements (e.g. Dapr state store needing an AES key) that
        // should not block OpenAPI generation or cold-path startup.
        var secretStore = _serviceProvider.GetRequiredService<ISecretStore>();
        var userAccessToken = await secretStore.ReadAsync(
            session.AccessTokenStoreKey, cancellationToken);
        if (string.IsNullOrEmpty(userAccessToken))
        {
            _logger.LogWarning(
                "list-repositories: session {SessionId} found but access token is missing " +
                "from the secret store; returning 401 missingOAuth (#1663)",
                sessionId);
            return await MissingOAuthResultAsync(
                "The GitHub OAuth session is missing its access token. " +
                "Re-link the operator's GitHub account to recover.",
                cancellationToken);
        }

        try
        {
            // Enumerate all App installations, then call
            // `GET /user/installations/{id}/repositories` with the user's
            // OAuth token for each one. GitHub enforces access control
            // server-side: the endpoint returns only the repos within that
            // installation that the calling user can actually see. This means:
            //   • Personal-account installations: user's own repos
            //   • Org installations: repos the user can access as a member
            //     or direct collaborator, including private repos
            //
            // We intentionally do NOT pre-filter installations by the user's
            // org list (the previous approach via IGitHubUserScopeResolver /
            // GET /user/orgs). That pre-filter required the `read:org` OAuth
            // scope to resolve private org memberships; without it, org
            // installations were silently dropped even for org members/admins.
            // Delegating the access check to the per-installation GitHub API
            // call avoids the scope requirement and lets GitHub be the
            // authority on what the user can see.
            //
            // A failure on one installation MUST NOT poison the list —
            // log it and keep the other installations' rows so the
            // wizard still has something to render.
            var installations = await _installationsClient
                .ListInstallationsAsync(cancellationToken);

            _logger.LogInformation(
                "list-repositories: enumerating {Count} installation(s) for session {SessionId} (login={Login})",
                installations.Count, sessionId, session.Login);

            var aggregated = new List<GitHubRepositoryResponse>();
            foreach (var installation in installations)
            {
                IReadOnlyList<GitHubInstallationRepository> repos;
                try
                {
                    repos = await _installationsClient
                        .ListUserAccessibleRepositoriesAsync(
                            installation.InstallationId, userAccessToken, cancellationToken);
                }
                catch (Octokit.AuthorizationException ex)
                    when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // 401 means the token itself is invalid or revoked —
                    // surface this so the portal prompts re-authorization.
                    _logger.LogWarning(ex,
                        "list-repositories: user OAuth token rejected (401) " +
                        "while enumerating installation {InstallationId}; returning 401 missingOAuth",
                        installation.InstallationId);
                    return await MissingOAuthResultAsync(
                        "GitHub rejected the OAuth token (it may have been " +
                        "revoked). Re-link the operator's GitHub account.",
                        cancellationToken);
                }
                catch (Octokit.AuthorizationException ex)
                {
                    // 403: user simply does not have access to this
                    // installation (e.g. not a member of the org). Skip it;
                    // don't fail the entire request.
                    _logger.LogInformation(ex,
                        "list-repositories: user lacks access to installation {InstallationId} ({Account}); skipping",
                        installation.InstallationId, installation.Account);
                    continue;
                }
                catch (Octokit.RateLimitExceededException ex)
                {
                    _logger.LogWarning(ex,
                        "list-repositories: user OAuth token rate-limited while " +
                        "enumerating installation {InstallationId}",
                        installation.InstallationId);
                    return Results.Problem(
                        title: "GitHub rate limit exceeded",
                        detail: "The user's OAuth token is rate-limited; retry shortly.",
                        statusCode: StatusCodes.Status429TooManyRequests);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to list repositories for installation {InstallationId} ({Account}); skipping",
                        installation.InstallationId, installation.Account);
                    continue;
                }

                // Run the per-installation result through the pure
                // intersection helper (#1663). With a null user-set
                // this is a no-op besides the canonical alphabetical
                // sort; the helper exists so the cloud overlay can
                // wedge in tenant-scoped allow-lists at this seam
                // without re-implementing the rule.
                var filtered = UserScopedRepositoryFilter
                    .Intersect(repos, userAccessibleRepoIds: null);
                foreach (var repo in filtered)
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
        catch (Octokit.AuthorizationException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // App JWT was rejected by GitHub — this is a credential/config
            // problem, not a per-user OAuth problem. Surface it distinctly so
            // the portal can render the connector's disabled state.
            _logger.LogError(ex,
                "list-repositories: App JWT rejected by GitHub (Bad credentials). " +
                "Verify GitHub__AppId={AppId} and GitHub__PrivateKeyPem in spring.env.",
                _options.Value.AppId);
            return Results.Problem(
                title: "GitHub App credentials rejected",
                detail: "The GitHub App JWT was rejected by GitHub. Verify GitHub__AppId and GitHub__PrivateKeyPem match the registered App.",
                statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?> { ["disabled"] = true, ["reason"] = "App credentials rejected by GitHub" });
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

    private static IResult GetUserConfigSchemaEndpointAsync()
    {
        return Results.Ok(BuildUserConfigSchema());
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
            config.Repo,
            config.AppInstallationId,
            config.PatSecretName,
            eventsAreDefault ? DefaultEvents : config.Events!,
            config.Reviewer,
            eventsAreDefault,
            config.AddOnAssign,
            config.RemoveOnAssign,
            config.IncludeLabels,
            config.ExcludeLabels,
            config.IncludeAuthors,
            config.IncludePaths);
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
          "required": ["repo"],
          "properties": {
            "repo": { "type": "string", "description": "The qualified repository name in 'owner/repo' form (ADR-0047 §11)." },
            "appInstallationId": {
              "type": ["integer", "null"],
              "description": "The GitHub App installation id powering the binding. Exactly one of 'appInstallationId' or 'pat_secret_name' MUST be set (ADR-0047 §11); the binding-create endpoint rejects neither/both with GitHubBindingAuthRequired / GitHubBindingAuthAmbiguous."
            },
            "pat_secret_name": {
              "type": ["string", "null"],
              "description": "Tenant-secret name addressing the PAT the binding pushes with (ADR-0047 §5). Exactly one of 'appInstallationId' or 'pat_secret_name' MUST be set; the resolver looks up the secret by name at outbound-call time through ISecretResolver."
            },
            "events": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Webhook events to subscribe to. Null falls back to the connector's default set."
            },
            "reviewer": {
              "type": ["string", "null"],
              "description": "Default GitHub login (no leading @) requested as the reviewer on pull requests opened by this unit."
            },
            "add_on_assign": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Labels to add when an issue is assigned."
            },
            "remove_on_assign": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Labels to remove when an issue is assigned."
            },
            "include_labels": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Inbound webhook filter: labels that gate delivery (disjunctive). Empty / null means no filter on this kind. Issue #2407."
            },
            "exclude_labels": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Inbound webhook filter: labels that drop delivery (evaluated first). Empty / null means no filter on this kind. Issue #2407."
            },
            "include_authors": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Inbound webhook filter: GitHub logins that gate delivery (disjunctive). Empty / null means no filter on this kind. Issue #2407."
            },
            "include_paths": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Inbound webhook filter: file-path prefixes that gate PR-shape delivery (disjunctive). Ignored for pure issue events. Empty / null means no filter on this kind. Issue #2407."
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }

    // Hand-authored schema mirroring GitHubUserConfig per ADR-0047 §4.
    // Strictly display-identity: { username (required), display_handle?
    // (optional) }. Built the same way as BuildConfigSchema so the two
    // surfaces stay structurally aligned — if either ever drifts from its
    // CLR record the contract tests will catch the mismatch.
    private static JsonElement BuildUserConfigSchema()
    {
        const string schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "GitHubUserConfig",
          "type": "object",
          "required": ["username"],
          "properties": {
            "username": {
              "type": "string",
              "description": "The GitHub login for this TenantUser (without the leading '@'). Required (ADR-0047 §4). Used for @-mention rendering, --add-reviewer invocations, and attribution."
            },
            "display_handle": {
              "type": ["string", "null"],
              "description": "Optional human-friendly rendering (e.g. 'Alice Smith (@alice)'). When null, render sites fall back to 'username'."
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }
}

// GitHubConnectorRuntime removed in #2456. The platform no longer
// installs per-repo GitHub webhooks, so there is no hook id or
// installation id to persist on the binding row. Runtime metadata
// storage (IUnitConnectorRuntimeStore) stays as a generic
// connector-agnostic seam — GitHub simply no longer uses it.
