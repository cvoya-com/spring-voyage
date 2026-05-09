// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves which installation, if any, covers the given repo. Returns an
/// explicit "not installed" result — rather than throwing — when the App is
/// not installed for that repo so operator UIs can render a "click here to
/// install" call-to-action.
/// </summary>
public class FindInstallationForRepoSkill(
    IGitHubInstallationsClient installations,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<FindInstallationForRepoSkill>();

    /// <summary>
    /// Finds the installation for the given repo.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        _logger.LogInformation("Finding installation for {Owner}/{Repo}", owner, repo);

        var installation = await installations.FindInstallationForRepoAsync(owner, repo, cancellationToken);

        if (installation is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                installed = false,
                owner,
                repo,
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            installed = true,
            owner,
            repo,
            installation_id = installation.InstallationId,
            account = installation.Account,
            account_type = installation.AccountType,
            repo_selection = installation.RepoSelection,
        });
    }
}
