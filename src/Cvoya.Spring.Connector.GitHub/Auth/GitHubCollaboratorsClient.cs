// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubCollaboratorsClient"/> implementation backed
/// by Octokit. Authenticates as the chosen installation (mints an access
/// token via <see cref="GitHubAppAuth"/> + <see cref="IInstallationTokenCache"/>)
/// and calls Octokit's
/// <see cref="IRepoCollaboratorsClient.GetAll(string, string)"/> — the
/// same wire shape <c>GET /repos/{owner}/{repo}/collaborators</c>
/// returns.
/// </summary>
public class GitHubCollaboratorsClient(
    GitHubAppAuth auth,
    IInstallationTokenCache tokenCache,
    ILoggerFactory loggerFactory) : IGitHubCollaboratorsClient
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubCollaboratorsClient>();

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<GitHubCollaborator>> ListCollaboratorsAsync(
        long installationId,
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var minted = await tokenCache.GetOrMintAsync(
            installationId,
            (id, ct) => auth.MintInstallationTokenAsync(id, ct),
            cancellationToken);

        var client = new GitHubClient(new ProductHeaderValue("SpringVoyage"))
        {
            Credentials = new Credentials(minted.Token),
        };

        var collaborators = await client.Repository.Collaborator.GetAll(owner, repo);

        _logger.LogInformation(
            "Repo {Owner}/{Repo} has {Count} collaborator(s) visible to installation {InstallationId}",
            owner, repo, collaborators.Count, installationId);

        return collaborators
            .Where(c => !string.IsNullOrEmpty(c.Login))
            .Select(c => new GitHubCollaborator(c.Login, c.AvatarUrl))
            .ToList();
    }
}