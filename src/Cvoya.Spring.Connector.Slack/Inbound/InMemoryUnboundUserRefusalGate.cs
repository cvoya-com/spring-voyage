// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

using System.Collections.Concurrent;

/// <summary>
/// OSS-default <see cref="IUnboundUserRefusalGate"/>. Thread-safe
/// in-memory set keyed on <c>(team_id, slack_user_id)</c>. Forgets
/// state on restart — acceptable for OSS per ADR-0061 §2.4.
/// </summary>
public sealed class InMemoryUnboundUserRefusalGate : IUnboundUserRefusalGate
{
    private readonly ConcurrentDictionary<string, byte> _refused = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool TryClaimRefusal(string teamId, string slackUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slackUserId);

        var key = teamId + "|" + slackUserId;
        return _refused.TryAdd(key, 0);
    }
}
