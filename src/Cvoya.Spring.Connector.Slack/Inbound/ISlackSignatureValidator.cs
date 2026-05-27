// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

/// <summary>
/// Validates a Slack request signature (events + slash commands)
/// using HMAC-SHA256 against the binding's signing secret per
/// <see href="https://api.slack.com/authentication/verifying-requests-from-slack">Slack's verifying-requests-from-slack contract</see>.
///
/// <para>
/// Wire shape:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Header <c>X-Slack-Request-Timestamp</c>: unix-seconds. Reject
///     when <c>now - timestamp</c> falls outside ±5 minutes (replay
///     protection).
///   </description></item>
///   <item><description>
///     Header <c>X-Slack-Signature</c>: <c>v0=&lt;hex&gt;</c> where
///     <c>hex</c> is HMAC-SHA256 of the literal
///     <c>v0:&lt;timestamp&gt;:&lt;rawBody&gt;</c> keyed on the
///     binding's <c>signing_secret</c>. Constant-time compared with
///     <c>CryptographicOperations.FixedTimeEquals</c>.
///   </description></item>
/// </list>
/// </summary>
public interface ISlackSignatureValidator
{
    /// <summary>
    /// Validates a Slack request signature. Returns <c>true</c> only
    /// when the timestamp is in window AND the signature matches.
    /// </summary>
    /// <param name="rawBody">The raw request body as the client sent it.</param>
    /// <param name="timestamp">Value of <c>X-Slack-Request-Timestamp</c>.</param>
    /// <param name="signature">Value of <c>X-Slack-Signature</c>.</param>
    /// <param name="signingSecret">The binding's signing secret.</param>
    /// <param name="now">
    /// Current time for the ±5-minute window comparison. Defaults to
    /// <see cref="TimeProvider.System"/>'s now when <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> when both checks pass; <c>false</c> otherwise. The
    /// caller surfaces a 401 with no body on a <c>false</c> result.
    /// </returns>
    bool Validate(
        string rawBody,
        string? timestamp,
        string? signature,
        string signingSecret,
        DateTimeOffset? now = null);
}
