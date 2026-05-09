// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Lists every installation the GitHub App can currently see. Calls
/// <see cref="IGitHubInstallationsClient.ListInstallationsAsync"/>, which
/// authenticates with the App JWT rather than an installation token — the
/// listing endpoint lives above any single installation.
/// </summary>
public class ListInstallationsSkill(
    IGitHubInstallationsClient installations,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListInstallationsSkill>();

    /// <summary>
    /// Lists all App installations.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing GitHub App installations");

        var list = await installations.ListInstallationsAsync(cancellationToken);

        return JsonSerializer.SerializeToElement(new
        {
            count = list.Count,
            installations = list.Select(i => new
            {
                id = i.InstallationId,
                account = i.Account,
                account_type = i.AccountType,
                repo_selection = i.RepoSelection,
            }).ToArray(),
        });
    }
}
