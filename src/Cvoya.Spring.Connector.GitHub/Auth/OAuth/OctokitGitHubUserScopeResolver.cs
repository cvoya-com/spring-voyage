// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubUserScopeResolver"/> backed by Octokit. Calls
/// <c>GET /user</c> to resolve the authenticated login and
/// <c>GET /user/orgs</c> to enumerate the organisations the user belongs
/// to, then returns the union so the <c>list-repositories</c> endpoint can
/// filter App installations to only those the calling user is allowed to see.
/// </summary>
public class OctokitGitHubUserScopeResolver(ILoggerFactory loggerFactory) : IGitHubUserScopeResolver
{
    private static readonly ProductHeaderValue UserAgent = new("SpringVoyage-GitHubConnector");
    private readonly ILogger _logger = loggerFactory.CreateLogger<OctokitGitHubUserScopeResolver>();

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ResolveAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var client = new GitHubClient(UserAgent)
        {
            Credentials = new Credentials(accessToken),
        };

        // GET /user — the user's own login.
        var user = await client.User.Current();
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { user.Login };

        _logger.LogDebug(
            "Resolved GitHub user scope: login={Login}; fetching org memberships",
            user.Login);

        // GET /user/orgs — organisations the authenticated user belongs to.
        // This requires the read:org scope; if the token does not have it
        // the call returns an empty list rather than failing, which is
        // acceptable — the user will only see personal-account installations.
        try
        {
            var orgs = await client.Organization.GetAllForCurrent();
            foreach (var org in orgs)
            {
                logins.Add(org.Login);
            }

            _logger.LogDebug(
                "GitHub user {Login} belongs to {OrgCount} organisation(s): {Orgs}",
                user.Login, orgs.Count, string.Join(", ", orgs.Select(o => o.Login)));
        }
        catch (Exception ex)
        {
            // Missing read:org scope or rate limit — log and continue.
            // The caller will only see personal-account installations in
            // this case, which is safer than failing the entire request.
            _logger.LogWarning(ex,
                "Could not enumerate organisation memberships for {Login}; " +
                "falling back to personal-account scope only. " +
                "Grant the 'read:org' scope to include organisation repositories.",
                user.Login);
        }

        return logins;
    }
}