// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record ListChildrenRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("threadId")] Guid ThreadId);

/// <summary>
/// Wire envelope returned by <c>list_children</c>. Matches the descriptor
/// shape advertised by <c>list_children.output.schema.json</c> per
/// ADR-0039 §3.
/// </summary>
public sealed record ListChildrenResponse(
    [property: JsonPropertyName("children")] OrchestrationChildDescriptorPayload[] Children);

public sealed record OrchestrationChildDescriptorPayload(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("executionConfig")] JsonElement? ExecutionConfig);

public sealed record InspectChildRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("targetAddress")] string TargetAddress,
    [property: JsonPropertyName("threadId")] Guid ThreadId);

public sealed record InspectChildResponse(
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object?> Metadata);

public sealed record DelegateToChildRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("targetAddress")] string TargetAddress,
    [property: JsonPropertyName("threadId")] Guid ThreadId,
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageContent")] string MessageContent,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record DelegateToChildResponse(
    [property: JsonPropertyName("message")] OrchestrationCallbackMessage? Message);

public sealed record FanoutToChildrenRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("targetAddresses")] string[] TargetAddresses,
    [property: JsonPropertyName("threadId")] Guid ThreadId,
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageContent")] string MessageContent,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record FanoutToChildrenResponse(
    [property: JsonPropertyName("results")] FanoutTargetResult[] Results);

public sealed record FanoutTargetResult(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("message")] OrchestrationCallbackMessage? Message);

public sealed record QueryChildStatusRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("targetAddress")] string TargetAddress,
    [property: JsonPropertyName("threadId")] Guid ThreadId);

/// <summary>
/// Wire envelope returned by <c>query_child_status</c>. Matches
/// <c>query_child_status.output.schema.json</c> per ADR-0039 §3:
/// <c>status</c> is required, <c>lastActivityAt</c> and <c>busyOnThread</c>
/// are optional and are emitted only when known.
/// </summary>
public sealed record QueryChildStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastActivityAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastActivityAt = null,
    [property: JsonPropertyName("busyOnThread"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BusyOnThread = null);

public sealed record OrchestrationCallbackMessage(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("fromAddress")] string FromAddress,
    [property: JsonPropertyName("toAddress")] string ToAddress,
    [property: JsonPropertyName("threadId")] string? ThreadId,
    [property: JsonPropertyName("messageContent")] string MessageContent);

public sealed record OrchestrationCallbackErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);
