// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Lists GitHub App installations visible to the currently-configured App.
/// Extracted as an abstraction so the private (cloud) repo can substitute a
/// tenant-scoped implementation — e.g. one that filters installations by the
/// caller's OAuth identity — without touching endpoint code.
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