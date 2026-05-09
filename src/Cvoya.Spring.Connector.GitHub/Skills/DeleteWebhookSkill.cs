// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Octokit;

/// <summary>
/// Deletes a repository webhook by id. Unlike the registrar's
/// <c>UnregisterAsync</c> — which intentionally swallows 404s so unit teardown
/// cannot be blocked by a stale hook id — this skill surfaces 404 to the
/// caller as a structured "not found" result so operator UIs can distinguish
/// "hook gone" from "hook deleted successfully".
/// </summary>
public class DeleteWebhookSkill(IGitHubClient gitHubClient, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DeleteWebhookSkill>();

    /// <summary>
    /// Deletes the specified webhook.
    /// </summary>
    public async Task<JsonElement> ExecuteAsync(
        string owner,
        string repo,
        long hookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var hookIdInt = checked((int)hookId);

        _logger.LogInformation(
            "Deleting webhook {HookId} on {Owner}/{Repo}",
            hookId, owner, repo);

        try
        {
            await gitHubClient.Repository.Hooks.Delete(owner, repo, hookIdInt);
        }
        catch (NotFoundException)
        {
            return JsonSerializer.SerializeToElement(new
            {
                deleted = false,
                reason = "not_found",
                hook_id = hookId,
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            deleted = true,
            hook_id = hookId,
        });
    }
}
