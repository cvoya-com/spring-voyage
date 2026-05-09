// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using Octokit;

/// <summary>
/// Maps an Octokit <see cref="RepositoryHook"/> to the shape the webhook
/// skills return. Kept deliberately thin so the JSON emitted matches the
/// GitHub API response fields agents already know.
/// </summary>
internal static class WebhookProjection
{
    public static object Project(RepositoryHook hook) => new
    {
        id = hook.Id,
        name = hook.Name,
        active = hook.Active,
        events = hook.Events?.ToArray() ?? [],
        config = new
        {
            url = hook.Config?.TryGetValue("url", out var url) == true ? url : null,
            content_type = hook.Config?.TryGetValue("content_type", out var ct) == true ? ct : null,
            insecure_ssl = hook.Config?.TryGetValue("insecure_ssl", out var ssl) == true ? ssl : null,
        },
        created_at = hook.CreatedAt,
        updated_at = hook.UpdatedAt,
        url = hook.Url,
    };
}
