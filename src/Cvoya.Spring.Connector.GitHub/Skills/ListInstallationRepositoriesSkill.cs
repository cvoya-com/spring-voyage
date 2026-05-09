// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Lists the repositories a specific installation has access to. Internally
/// this mints an installation-scoped token (via the shared cache) because the
/// underlying endpoint requires installation auth.
/// </summary>
public class ListInstallationRepositoriesSkill(
    IGitHubInstallationsClient installations,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListInstallationRepositoriesSkill>();

    /// <summary>
    /// Lists repos covered by the installation.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing repositories for installation {InstallationId}", installationId);

        var repos = await installations.ListInstallationRepositoriesAsync(installationId, cancellationToken);

        return JsonSerializer.SerializeToElement(new
        {
            installation_id = installationId,
            count = repos.Count,
            repositories = repos.Select(r => new
            {
                id = r.RepositoryId,
                owner = r.Owner,
                name = r.Name,
                full_name = r.FullName,
                @private = r.Private,
            }).ToArray(),
        });
    }
}
