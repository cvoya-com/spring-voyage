// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack;

using System.Text.Json;

using Cvoya.Spring.Connectors;

/// <summary>
/// Decodes the bound-user list from a persisted Slack binding's
/// opaque <c>Config</c> payload. Registered as a singleton
/// <see cref="ITenantBoundUserExtractor"/> so the platform's
/// <see cref="ITenantConnectorBindingStore.GetBoundUsersAsync"/>
/// dispatches by slug per ADR-0061 §7.7.
///
/// <para>
/// ADR-0061 §7.1: the result is a list — length 1 in OSS, length N
/// in cloud. No code assumes a singleton.
/// </para>
/// </summary>
public class SlackBoundUserExtractor : ITenantBoundUserExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public bool Handles(string connectorSlug) =>
        string.Equals(connectorSlug, "slack", StringComparison.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<TenantBoundUser> Extract(TenantConnectorBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions);
        if (config is null || config.BoundUsers.Count == 0)
        {
            return Array.Empty<TenantBoundUser>();
        }

        var result = new List<TenantBoundUser>(config.BoundUsers.Count);
        foreach (var bound in config.BoundUsers)
        {
            result.Add(new TenantBoundUser(bound.SlackUserId, bound.TenantUserId));
        }
        return result;
    }
}
