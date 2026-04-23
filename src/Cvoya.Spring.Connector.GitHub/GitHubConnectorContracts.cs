// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

/// <summary>
/// Request body for
/// <c>PUT /api/v1/connectors/github/units/{unitId}/config</c>. Binds the
/// unit to the GitHub connector and upserts the per-unit config atomically.
/// </summary>
/// <param name="Owner">The repository owner (user or organization login).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="AppInstallationId">The GitHub App installation id powering the binding, if any.</param>
/// <param name="Events">Webhook events to subscribe to. Null falls back to the connector's default set.</param>
/// <param name="Reviewer">
/// Default GitHub login (no leading <c>@</c>) requested as the reviewer on
/// pull requests opened by this unit. Optional — agents that pass a
/// reviewer explicitly still override per-call.
/// </param>
public record UnitGitHubConfigRequest(
    string Owner,
    string Repo,
    long? AppInstallationId = null,
    IReadOnlyList<string>? Events = null,
    string? Reviewer = null);

/// <summary>
/// Response body for
/// <c>GET</c>/<c>PUT /api/v1/connectors/github/units/{unitId}/config</c>.
/// Returns the unit id and the effective config (with <see cref="Events"/>
/// resolved to the connector's defaults when the caller didn't supply one).
/// </summary>
/// <param name="UnitId">The unit id this config is bound to.</param>
/// <param name="Owner">The repository owner (user or organization login).</param>
/// <param name="Repo">The repository name.</param>
/// <param name="AppInstallationId">The GitHub App installation id powering the binding, if any.</param>
/// <param name="Events">The effective webhook event subscriptions.</param>
/// <param name="Reviewer">
/// Default reviewer login persisted on the binding, or <c>null</c> when
/// the unit didn't pick one. Surfaced verbatim — the response does not
/// invent a default.
/// </param>
public record UnitGitHubConfigResponse(
    string UnitId,
    string Owner,
    string Repo,
    long? AppInstallationId,
    IReadOnlyList<string> Events,
    string? Reviewer);

/// <summary>
/// Response item for
/// <c>GET /api/v1/connectors/github/actions/list-installations</c>.
/// </summary>
/// <param name="InstallationId">The numeric installation id.</param>
/// <param name="Account">The account login the App is installed on.</param>
/// <param name="AccountType">Either <c>User</c> or <c>Organization</c>.</param>
/// <param name="RepoSelection">Either <c>all</c> or <c>selected</c>.</param>
public record GitHubInstallationResponse(
    long InstallationId,
    string Account,
    string AccountType,
    string RepoSelection);

/// <summary>
/// Response body for
/// <c>GET /api/v1/connectors/github/actions/install-url</c>.
/// </summary>
/// <param name="Url">The install URL the user should be sent to.</param>
public record GitHubInstallUrlResponse(string Url);

/// <summary>
/// Response item for
/// <c>GET /api/v1/connectors/github/actions/list-repositories</c>. One
/// entry per repository the GitHub App can currently see, aggregated
/// across every visible installation. The wizard renders the
/// <see cref="FullName"/> in a single dropdown, splits client-side, and
/// posts <see cref="Owner"/> + <see cref="Repo"/> + <see cref="InstallationId"/>
/// back on the typed config — replacing the v1-style three-control surface
/// (manual owner / manual repo / installation picker) with one selection.
/// </summary>
/// <param name="InstallationId">
/// The installation id the App authenticates as for this repo. Carried in
/// the response so the wizard never has to call back to resolve it from
/// owner+repo (the existing <c>FindInstallationForRepoAsync</c> path).
/// </param>
/// <param name="RepositoryId">The numeric repository id (informational; not persisted on the binding).</param>
/// <param name="Owner">The repository owner login.</param>
/// <param name="Repo">The repository short name.</param>
/// <param name="FullName">The <c>owner/name</c> combined form — the dropdown label.</param>
/// <param name="Private">Whether the repo is private — surfaced so the UI can render a lock icon.</param>
public record GitHubRepositoryResponse(
    long InstallationId,
    long RepositoryId,
    string Owner,
    string Repo,
    string FullName,
    bool Private);

/// <summary>
/// Response item for
/// <c>GET /api/v1/connectors/github/actions/list-collaborators</c>. The
/// reviewer-picker dropdown in the wizard binds to this — selecting a row
/// stores <see cref="Login"/> on <see cref="UnitGitHubConfigRequest.Reviewer"/>.
/// </summary>
/// <param name="Login">The GitHub login (without the leading <c>@</c>).</param>
/// <param name="AvatarUrl">The collaborator's avatar URL, if surfaced by GitHub.</param>
public record GitHubCollaboratorResponse(
    string Login,
    string? AvatarUrl);