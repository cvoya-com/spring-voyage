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
public record UnitGitHubConfigRequest(
    string Owner,
    string Repo,
    long? AppInstallationId = null,
    IReadOnlyList<string>? Events = null);

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
public record UnitGitHubConfigResponse(
    string UnitId,
    string Owner,
    string Repo,
    long? AppInstallationId,
    IReadOnlyList<string> Events);

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