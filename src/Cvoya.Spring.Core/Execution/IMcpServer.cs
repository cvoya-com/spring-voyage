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
    /// <paramref name="callerKind"/> records whether the bound caller is an
    /// agent or a unit so platform tools (e.g. the Spring Voyage directory
    /// tools, #2231) can answer <c>get_self()</c>-style queries without a
    /// follow-up DB lookup; defaults to <c>"agent"</c> to preserve the
    /// pre-#2231 caller shape for any code path that hasn't yet been updated.
    /// </summary>
    McpSession IssueSession(string agentId, string threadId, string callerKind = "agent");

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
/// <param name="CallerKind">
/// Either <c>"agent"</c> or <c>"unit"</c>; carries the same value as the
/// corresponding scheme constant on
/// <see cref="Cvoya.Spring.Core.Messaging.Address"/>. Defaults to
/// <c>"agent"</c> so positional construction in tests written before
/// #2231 keeps compiling.
/// </param>
public record McpSession(string Token, string AgentId, string ThreadId, string CallerKind = "agent");
