// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using System.Net;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubInstallationsClient"/> implementation backed by
/// Octokit. Lists installations with the App JWT, resolves repo → installation
/// with the same JWT, and lists repositories for a specific installation with
/// that installation's access token (minted via <see cref="GitHubAppAuth"/>).
/// </summary>
public class GitHubInstallationsClient(
    GitHubAppAuth auth,
    IInstallationTokenCache tokenCache,
    ILoggerFactory loggerFactory) : IGitHubInstallationsClient
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubInstallationsClient>();

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<GitHubInstallation>> ListInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        var client = CreateAppJwtClient();

        // Octokit's GetAllForCurrent authenticates against GET /app/installations,
        // which requires the App JWT (NOT an installation token). Returning the
        // raw Octokit list lets the default impl stay thin; the private cloud
        // repo is free to filter / reshape.
        var installations = await client.GitHubApps.GetAllInstallationsForCurrent();

        _logger.LogInformation(
            "GitHub App sees {Count} installation(s)",
            installations.Count);

        return installations
            .Select(MapInstallation)
            .ToList();
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<GitHubInstallationRepository>> ListInstallationRepositoriesAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        // `GET /installation/repositories` requires an installation token, not
        // the App JWT. Reuse the shared cache so repeat calls within a host
        // don't re-mint.
        var minted = await tokenCache.GetOrMintAsync(
            installationId,
            (id, ct) => auth.MintInstallationTokenAsync(id, ct),
            cancellationToken);

        var installationClient = new GitHubClient(new ProductHeaderValue("SpringVoyage"))
        {
            Credentials = new Credentials(minted.Token),
        };

        var response = await installationClient.GitHubApps.Installation
            .GetAllRepositoriesForCurrent();

        _logger.LogInformation(
            "Installation {InstallationId} covers {Count} repositor(y|ies)",
            installationId, response.TotalCount);

        return response.Repositories
            .Select(r => new GitHubInstallationRepository(
                r.Id,
                r.Owner?.Login ?? string.Empty,
                r.Name ?? string.Empty,
                r.FullName ?? string.Empty,
                r.Private))
            .ToList();
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<GitHubInstallationRepository>> ListUserAccessibleRepositoriesAsync(
        long installationId,
        string userAccessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userAccessToken);

        // `GET /user/installations/{installation_id}/repositories` returns
        // the repos under a given installation that the OAuth user can see.
        // It uses the user access token, NOT an installation token — that's
        // the whole point: cross-checking the App's coverage against the
        // user's own permissions so a private repo the App can reach but the
        // user cannot access is filtered out.
        //
        // Octokit has no typed wrapper for this endpoint as of v13, so we go
        // through Connection.Get<T> with a local DTO. Pagination is handled by
        // requesting per_page=100 and looping while the response has a
        // RepositoryCount > what we have so far. GitHub caps installations at
        // a reasonable size for the UI use case.
        var userClient = new GitHubClient(new ProductHeaderValue("SpringVoyage"))
        {
            Credentials = new Credentials(userAccessToken),
        };

        var aggregated = new List<GitHubInstallationRepository>();
        var seen = new HashSet<long>();
        var page = 1;
        const int PerPage = 100;
        var uri = new Uri(
            $"user/installations/{installationId}/repositories",
            UriKind.Relative);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new Dictionary<string, string>
            {
                ["per_page"] = PerPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            var response = await userClient.Connection
                .Get<UserInstallationRepositoriesResponse>(uri, parameters, accepts: null, cancellationToken)
                .ConfigureAwait(false);
            var payload = response.Body;
            if (payload?.Repositories is null || payload.Repositories.Count == 0)
            {
                break;
            }

            foreach (var repo in payload.Repositories)
            {
                if (!seen.Add(repo.Id))
                {
                    continue;
                }
                aggregated.Add(new GitHubInstallationRepository(
                    repo.Id,
                    repo.Owner?.Login ?? string.Empty,
                    repo.Name ?? string.Empty,
                    repo.FullName ?? string.Empty,
                    repo.Private));
            }

            if (payload.Repositories.Count < PerPage)
            {
                break;
            }
            page++;
        }

        _logger.LogInformation(
            "User-scoped installation {InstallationId} surfaces {Count} repositor(y|ies)",
            installationId, aggregated.Count);

        return aggregated;
    }

    /// <inheritdoc />
    public virtual async Task<GitHubInstallation?> FindInstallationForRepoAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var client = CreateAppJwtClient();

        try
        {
            var installation = await client.GitHubApps.GetRepositoryInstallationForCurrent(owner, repo);
            return MapInstallation(installation);
        }
        catch (NotFoundException)
        {
            // 404 means the App is not installed for this repo. That's a
            // first-class "not an error" signal; surface as null so callers
            // can choose whether to treat it as a user-facing problem.
            _logger.LogInformation(
                "GitHub App is not installed for {Owner}/{Repo}",
                owner, repo);
            return null;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Older Octokit versions surface some 404s as ApiException rather
            // than NotFoundException; belt and braces.
            _logger.LogInformation(
                "GitHub App is not installed for {Owner}/{Repo}",
                owner, repo);
            return null;
        }
    }

    private GitHubClient CreateAppJwtClient()
    {
        var jwt = auth.GenerateJwt();
        return new GitHubClient(new ProductHeaderValue("SpringVoyage"))
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer),
        };
    }

    private static GitHubInstallation MapInstallation(Installation i) =>
        new(
            i.Id,
            i.Account?.Login ?? string.Empty,
            // Both TargetType and RepositorySelection are Octokit StringEnum<T>
            // — their StringValue preserves the exact server-side spelling
            // ("User" / "Organization" and "all" / "selected"), matching the
            // endpoint contract documented on GitHubInstallation.
            i.TargetType.StringValue ?? "User",
            i.RepositorySelection.StringValue ?? "all");

    // Local DTOs for `GET /user/installations/{installation_id}/repositories`.
    // Octokit (14.x) lacks a typed wrapper for this user-scoped endpoint, so
    // we deserialize through Octokit's SimpleJson which maps JSON snake_case
    // to PascalCase property names by convention — no explicit attributes
    // needed (e.g. `full_name` → `FullName`).
    private sealed class UserInstallationRepositoriesResponse
    {
        public int TotalCount { get; set; }
        public List<UserInstallationRepository> Repositories { get; set; } = new();
    }

    private sealed class UserInstallationRepository
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? FullName { get; set; }
        public bool Private { get; set; }
        public UserInstallationRepositoryOwner? Owner { get; set; }
    }

    private sealed class UserInstallationRepositoryOwner
    {
        public string? Login { get; set; }
    }
}