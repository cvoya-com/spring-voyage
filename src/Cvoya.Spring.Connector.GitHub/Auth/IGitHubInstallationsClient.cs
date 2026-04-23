// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Lists GitHub App installations visible to the currently-configured App and
/// resolves repo ↔ installation mappings. Extracted as an abstraction so the
/// private (cloud) repo can substitute a tenant-scoped implementation — e.g.
/// one that filters installations by the caller's OAuth identity — without
/// touching endpoint code.
/// </summary>
public interface IGitHubInstallationsClient
{
    /// <summary>
    /// Returns every installation the GitHub App can currently see. The
    /// default (single-tenant) implementation authenticates with the App JWT
    /// and calls <c>GET /app/installations</c>. A private cloud impl may
    /// scope the result to a tenant's installations by intersecting with
    /// per-user OAuth tokens.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The visible installations.</returns>
    Task<IReadOnlyList<GitHubInstallation>> ListInstallationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the repositories accessible to the given installation. Uses an
    /// installation-token authenticated client to call
    /// <c>GET /installation/repositories</c>.
    /// </summary>
    /// <param name="installationId">The numeric installation id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The repositories the installation covers.</returns>
    Task<IReadOnlyList<GitHubInstallationRepository>> ListInstallationRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves which installation, if any, covers the given repository.
    /// Calls <c>GET /repos/{owner}/{repo}/installation</c>. Returns
    /// <c>null</c> when the App is not installed for the repo (GitHub returns
    /// 404 in that case).
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The installation id / metadata, or <c>null</c> when not installed.</returns>
    Task<GitHubInstallation?> FindInstallationForRepoAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the App installations the signed-in GitHub user can access via
    /// the user-to-server token. Calls <c>GET /user/installations</c>. The
    /// result is the intersection of "every installation of the App" and
    /// "every installation the user is allowed to enumerate via the App"
    /// (the App owner sees all of them; a regular user sees only their own
    /// + the orgs they belong to that have installed the App).
    /// </summary>
    /// <param name="userAccessToken">A GitHub user-to-server OAuth token.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The user-accessible installations of the configured App.</returns>
    /// <remarks>
    /// Used by the wizard's repository-listing endpoint so the dropdown
    /// is scoped to the signed-in user (issue #1153). Cloud overlays may
    /// substitute a tenant-aware implementation that reads the user token
    /// from a per-tenant credential store rather than threading it through
    /// the call.
    /// </remarks>
    Task<IReadOnlyList<GitHubInstallation>> ListUserAccessibleInstallationsAsync(
        string userAccessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the repositories the signed-in GitHub user can access on the
    /// given installation. Calls
    /// <c>GET /user/installations/{installation_id}/repositories</c>. The
    /// per-installation result is the intersection of "every repo the
    /// installation covers" and "every repo the user can see" — exactly
    /// the projection the wizard needs (issue #1153).
    /// </summary>
    /// <param name="userAccessToken">A GitHub user-to-server OAuth token.</param>
    /// <param name="installationId">The numeric installation id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The user-accessible repositories on this installation.</returns>
    Task<IReadOnlyList<GitHubInstallationRepository>> ListUserAccessibleInstallationRepositoriesAsync(
        string userAccessToken,
        long installationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A GitHub App installation — the account that installed the App plus the
/// scope of the install (all repos vs. a selected subset). Kept transport-
/// shaped so it can flow straight into the HTTP API response without a
/// further mapping step.
/// </summary>
/// <param name="InstallationId">The numeric installation id.</param>
/// <param name="Account">The account login the App is installed on.</param>
/// <param name="AccountType">Either <c>User</c> or <c>Organization</c>.</param>
/// <param name="RepoSelection">Either <c>all</c> or <c>selected</c>.</param>
public record GitHubInstallation(
    long InstallationId,
    string Account,
    string AccountType,
    string RepoSelection);

/// <summary>
/// A repository accessible to a given GitHub App installation — the minimal
/// projection needed by the topology skills (full / short name and whether
/// the repo is private).
/// </summary>
/// <param name="RepositoryId">The numeric repository id.</param>
/// <param name="Owner">The repository owner login.</param>
/// <param name="Name">The repository short name.</param>
/// <param name="FullName">The <c>owner/name</c> combined form.</param>
/// <param name="Private">Whether the repo is private.</param>
public record GitHubInstallationRepository(
    long RepositoryId,
    string Owner,
    string Name,
    string FullName,
    bool Private);