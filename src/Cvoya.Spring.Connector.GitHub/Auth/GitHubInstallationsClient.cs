// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubInstallationsClient"/> implementation backed by
/// Octokit. Authenticates with the App JWT (not an installation token) and
/// lists every installation the App can currently see.
/// </summary>
public class GitHubInstallationsClient(
    GitHubAppAuth auth,
    ILoggerFactory loggerFactory) : IGitHubInstallationsClient
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubInstallationsClient>();

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<GitHubInstallation>> ListInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        var jwt = auth.GenerateJwt();

        var client = new GitHubClient(new ProductHeaderValue("SpringVoyage"))
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer),
        };

        // Octokit's GetAllForCurrent authenticates against GET /app/installations,
        // which requires the App JWT (NOT an installation token). Returning the
        // raw Octokit list lets the default impl stay thin; the private cloud
        // repo is free to filter / reshape.
        var installations = await client.GitHubApps.GetAllInstallationsForCurrent();

        _logger.LogInformation(
            "GitHub App sees {Count} installation(s)",
            installations.Count);

        return installations
            .Select(i => new GitHubInstallation(
                i.Id,
                i.Account?.Login ?? string.Empty,
                // Both TargetType and RepositorySelection are Octokit
                // StringEnum<T> — their StringValue preserves the exact
                // server-side spelling ("User" / "Organization" and
                // "all" / "selected"), which matches the endpoint contract
                // documented on GitHubInstallation.
                i.TargetType.StringValue ?? "User",
                i.RepositorySelection.StringValue ?? "all"))
            .ToList();
    }
}