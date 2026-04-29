// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// An in-process MCP server exposing Spring Voyage connector skills to external
/// agent containers. Implementations bind a local HTTP endpoint and authenticate
/// callers via short-lived bearer tokens issued by <see cref="IssueSession"/>.
/// </summary>
public interface IMcpServer
{
    /// <summary>
    /// The URL the containerized agent should connect to. Null until the server
    /// has been started (implementations are hosted services).
    /// </summary>
    string? Endpoint { get; }

    /// <summary>
    /// Issues a new session bound to a specific agent/thread. The returned
    /// <see cref="McpSession.Token"/> must be presented by the container on each
    /// MCP request; the server uses the bound session to attribute tool calls.
    /// </summary>
    McpSession IssueSession(string agentId, string threadId);

    /// <summary>Revokes a previously issued session.</summary>
    void RevokeSession(string token);
}

/// <summary>
/// A short-lived credential the dispatcher hands the container to authenticate
/// to the in-process MCP server.
/// </summary>
/// <param name="Token">Opaque bearer token.</param>
/// <param name="AgentId">Agent bound to this session.</param>
/// <param name="ThreadId">Thread bound to this session.</param>
public record McpSession(string Token, string AgentId, string ThreadId);