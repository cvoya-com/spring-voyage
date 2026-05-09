// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Protocol-agnostic description of a single skill invocation (#359 /
/// <c>ISkillInvoker</c> seam). A skill call carries (a) the skill name as
/// advertised by the expertise-directory-driven catalog, (b) the JSON
/// arguments shaped by the skill's declared input schema, and (c) the
/// platform-layer context that every governance decision (boundary,
/// permission, policy, activity) needs to run before dispatch.
/// </summary>
/// <remarks>
/// <para>
/// This record is deliberately free of <see cref="Message"/> — callers
/// (planners, MCP bridges, future A2A gateways) do NOT know about the
/// internal bus. The default <see cref="ISkillInvoker"/> implementation
/// translates this into a domain <see cref="Message"/> and dispatches through
/// <see cref="IMessageRouter"/>; an alternative implementation (tracked in
/// #539) will translate the same record into an A2A gateway call.
/// </para>
/// <para>
/// <paramref name="CorrelationId"/> is the caller's conversation handle. When
/// omitted the invoker generates a fresh id so downstream activity emission
/// still groups the call with its response.
/// </para>
/// </remarks>
/// <param name="SkillName">
/// The skill name as surfaced by the catalog (for the OSS default this is
/// <c>expertise/{slug}</c>). Callers must pass the exact name they resolved
/// from <see cref="ISkillRegistry.GetToolDefinitions"/>.
/// </param>
/// <param name="Arguments">
/// JSON-object-shaped arguments matching the tool's input schema. Empty
/// object for skills that take no parameters.
/// </param>
/// <param name="Caller">
/// Optional caller identity. When the invoker is being driven by an agent
/// turn the caller is typically that agent's address; unauthenticated HTTP
/// probes and test harnesses may leave this null. The default implementation
/// stamps <see cref="Message.From"/> with the caller (falling back to a
/// synthetic <c>skill://caller</c> origin when null).
/// </param>
/// <param name="CorrelationId">
/// Optional conversation id for the resulting <see cref="Message"/>. When
/// null the default invoker synthesises one so activity rows group cleanly.
/// </param>
public record SkillInvocation(
    string SkillName,
    JsonElement Arguments,
    Address? Caller = null,
    string? CorrelationId = null);

/// <summary>
/// Result of a single <see cref="ISkillInvoker.InvokeAsync"/> call.
/// Protocol-agnostic: the caller sees a JSON payload on success and a
/// machine-readable error code + message on failure. The internal
/// <see cref="Message"/> shape is NOT leaked — the default implementation
/// unwraps <see cref="Message.Payload"/> and projects routing errors onto
/// <see cref="ErrorCode"/>.
/// </summary>
/// <param name="IsSuccess">
/// <c>true</c> when the target accepted the call and returned a response
/// payload. Note that a successful invocation may still carry an empty JSON
/// payload when the skill is fire-and-forget.
/// </param>
/// <param name="Payload">
/// JSON payload from the target when <see cref="IsSuccess"/> is <c>true</c>;
/// default <see cref="JsonElement"/> otherwise.
/// </param>
/// <param name="ErrorCode">
/// Machine-readable code when <see cref="IsSuccess"/> is <c>false</c>. The
/// default invoker maps <see cref="RoutingError.Code"/> values through
/// (e.g. <c>ADDRESS_NOT_FOUND</c>, <c>PERMISSION_DENIED</c>) plus the
/// skill-layer codes <c>SKILL_NOT_FOUND</c> and <c>BOUNDARY_BLOCKED</c>.
/// </param>
/// <param name="ErrorMessage">Human-readable companion to <see cref="ErrorCode"/>.</param>
public record SkillInvocationResult(
    bool IsSuccess,
    JsonElement Payload,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    /// <summary>Builds a successful result with the supplied payload.</summary>
    public static SkillInvocationResult Success(JsonElement payload) =>
        new(true, payload);

    /// <summary>Builds a failure result with the supplied code and message.</summary>
    public static SkillInvocationResult Failure(string code, string message) =>
        new(false, default, code, message);
}
