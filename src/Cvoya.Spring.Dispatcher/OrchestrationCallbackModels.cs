// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Text.Json.Serialization;

public sealed record ListChildrenRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("threadId")] Guid ThreadId);

public sealed record ListChildrenResponse(
    [property: JsonPropertyName("children")] string[] Children);

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

public sealed record QueryChildStatusResponse(
    [property: JsonPropertyName("status")] string Status);

public sealed record OrchestrationCallbackMessage(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("fromAddress")] string FromAddress,
    [property: JsonPropertyName("toAddress")] string ToAddress,
    [property: JsonPropertyName("threadId")] string? ThreadId,
    [property: JsonPropertyName("messageContent")] string MessageContent);

public sealed record OrchestrationCallbackErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);
