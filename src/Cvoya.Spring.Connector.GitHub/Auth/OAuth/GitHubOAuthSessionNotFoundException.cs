// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Cvoya.Spring.Core;

/// <summary>
/// Thrown when an OAuth session lookup fails — either because the session
/// id is unknown or because the underlying access-token plaintext is no
/// longer available from the secret store.
/// </summary>
public class GitHubOAuthSessionNotFoundException : SpringException
{
    /// <summary>
    /// The session id the caller looked up. Kept on the exception so
    /// endpoint-level handlers can surface it in the ProblemDetails body
    /// without having to thread it through their own state.
    /// </summary>
    public string SessionId { get; }

    /// <summary>Creates a new exception.</summary>
    public GitHubOAuthSessionNotFoundException(string sessionId)
        : base($"OAuth session '{sessionId}' is unknown or has been revoked.")
    {
        SessionId = sessionId;
    }
}