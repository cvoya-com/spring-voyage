// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Raised when a GitHub App API call is rejected with a credentials error
/// (<c>401 Bad credentials</c>) <em>and</em> the local clock is grossly skewed
/// from GitHub's own clock — the real cause being a stale container clock, not
/// a bad App ID / private key.
/// </summary>
/// <remarks>
/// <para>
/// On macOS / Windows, Podman runs containers inside a libkrun/QEMU VM. When
/// the host sleeps the VM clock freezes and does not resync on resume, so every
/// container falls behind real time by the sleep duration.
/// <see cref="GitHubAppAuth.GenerateJwt"/> signs the GitHub App JWT with the
/// container clock; a skewed clock produces a JWT whose <c>exp</c> is already in
/// the past, and GitHub rejects it with the generic message
/// <c>Bad credentials</c>.
/// </para>
/// <para>
/// <see cref="GitHubConnectorType"/>'s endpoint catch-all surfaces
/// <see cref="System.Exception.Message"/> verbatim to the connector wizard, so
/// the message on this exception is written to be actionable on its own — it
/// tells the developer to resync the container/host clock rather than to chase
/// credentials.
/// </para>
/// </remarks>
public sealed class GitHubClockSkewException : Exception
{
    /// <summary>
    /// Initializes the exception with a clock-skew-specific, actionable message.
    /// </summary>
    /// <param name="message">The actionable, developer-facing message.</param>
    /// <param name="inner">The underlying GitHub API rejection.</param>
    public GitHubClockSkewException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
