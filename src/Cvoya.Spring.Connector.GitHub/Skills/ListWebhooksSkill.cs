// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Lists every repository webhook configured for a repo. Complements the
/// auto-registration path (<see cref="Webhooks.IGitHubWebhookRegistrar"/>) by
/// letting operators inspect hooks the platform registered plus any added
/// out-of-band.
/// </summary>
public class ListWebhooksSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ListWebhooksSkill>();

    /// <summary>
    /// Lists all repository webhooks for the given repo.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        _logger.LogInformation("Listing webhooks on {Owner}/{Repo}", owner, repo);

        var hooks = await gitHubClient.Repository.Hooks.GetAll(owner, repo);

        var result = new
        {
            owner,
            repo,
            count = hooks.Count,
            hooks = hooks.Select(WebhookProjection.Project).ToArray(),
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
