// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubUserFetcher"/>. Uses Octokit so the user-agent,
/// retry handling, and rate-limit surfaces stay consistent with the rest
/// of the connector.
/// </summary>
public class OctokitGitHubUserFetcher : IGitHubUserFetcher
{
    private static readonly ProductHeaderValue UserAgent = new("SpringVoyage-GitHubConnector");

    /// <inheritdoc />
    public async Task<GitHubUserIdentity> GetAsync(string accessToken, CancellationToken ct)
    {
        var client = new GitHubClient(UserAgent)
        {
            Credentials = new Credentials(accessToken),
        };

        var user = await client.User.Current();
        return new GitHubUserIdentity(
            Login: user.Login,
            Id: user.Id,
            Name: user.Name,
            Email: user.Email);
    }
}