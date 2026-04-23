// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Lists the collaborators of a single repository. Extracted as an
/// abstraction so the private (cloud) repo can substitute a tenant-aware
/// implementation — e.g. one that filters by the caller's permission
/// level — without touching endpoint code. The default implementation is
/// installation-token authenticated and calls
/// <c>GET /repos/{owner}/{repo}/collaborators</c>.
/// </summary>
public interface IGitHubCollaboratorsClient
{
    /// <summary>
    /// Lists collaborators on the given repository. The installation id
    /// identifies which App installation token to mint; the connector
    /// already knows this from the wizard's repository-dropdown selection
    /// so callers don't have to resolve it again. Returns an empty list
    /// for a repository the App can see but that has no collaborators
    /// surfaced through the installation token (rare — typically only
    /// happens when the App scopes don't include <c>Members: read</c>).
    /// </summary>
    /// <param name="installationId">The GitHub App installation id covering the repo.</param>
    /// <param name="owner">The repository owner login.</param>
    /// <param name="repo">The repository short name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The collaborators on the repository.</returns>
    Task<IReadOnlyList<GitHubCollaborator>> ListCollaboratorsAsync(
        long installationId,
        string owner,
        string repo,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A repository collaborator — kept transport-shaped so it can flow
/// straight into the HTTP API response without an extra mapping step.
/// </summary>
/// <param name="Login">The collaborator's GitHub login.</param>
/// <param name="AvatarUrl">The collaborator's avatar URL, if exposed by GitHub.</param>
public record GitHubCollaborator(
    string Login,
    string? AvatarUrl);