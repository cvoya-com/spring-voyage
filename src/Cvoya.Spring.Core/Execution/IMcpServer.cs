// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Messaging;

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
    /// follow-up DB lookup. <paramref name="agentId"/> MUST be a Guid-shaped
    /// id (canonical 32-char no-dash hex or dashed form) and
    /// <paramref name="callerKind"/> MUST be either <see cref="Address.AgentScheme"/>
    /// or <see cref="Address.UnitScheme"/>; the implementation materialises
    /// <see cref="McpSession.Subject"/> from these so the effective-grant
    /// gate (#2379) can call <c>IToolGrantResolver.ResolveAsync</c> for
    /// <c>tools/list</c> filtering and <c>tools/call</c> authorization.
    /// Implementations MUST throw when the inputs cannot materialise a
    /// subject — there is no fail-open path; every session has a Subject.
    /// </summary>
    /// <paramref name="messageId"/> is the inbound message the turn responds
    /// to; it carries the per-turn delivery authority the retired callback
    /// JWT used to provide (ADR-0051), reaching messaging tools through
    /// <see cref="McpSession.MessageId"/> / <c>ToolCallContext.MessageId</c>.
    /// Every dispatch call site already has the message in hand; synthetic
    /// launch paths that are not serving an inbound message pass
    /// <see cref="System.Guid.Empty"/>.
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="agentId"/> is not Guid-shaped or when
    /// <paramref name="callerKind"/> is not <see cref="Address.AgentScheme"/> /
    /// <see cref="Address.UnitScheme"/>.
    /// </exception>
    McpSession IssueSession(
        string agentId,
        string threadId,
        string callerKind = "agent",
        Guid messageId = default);

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
/// <param name="Subject">
/// Address of the subject this session is bound to — either an
/// <c>agent:&lt;guid&gt;</c> or <c>unit:&lt;guid&gt;</c>. Always populated;
/// session establishment fails if the (agentId, callerKind) pair cannot
/// materialise a valid Address. The MCP server's effective-grant gate
/// consults this to call <c>IToolGrantResolver.ResolveAsync</c> for
/// <c>tools/list</c> filtering and <c>tools/call</c> authorization,
/// so every session is enforceable.
/// </param>
/// <param name="MessageId">
/// The inbound message the turn is responding to. The MCP session is minted
/// per turn and revoked on turn-end, so it carries the full
/// <c>(tenant, agentAddress, threadId, messageId)</c> per-turn delivery
/// authority the retired callback JWT used to carry (ADR-0051). Messaging
/// tools read it through <c>ToolCallContext.MessageId</c> to stamp the
/// outgoing message. Defaults to <see cref="System.Guid.Empty"/> so
/// positional construction in tests written before ADR-0051 keeps compiling.
/// </param>
public record McpSession(
    string Token,
    string AgentId,
    string ThreadId,
    string CallerKind,
    Address Subject,
    Guid MessageId = default);
