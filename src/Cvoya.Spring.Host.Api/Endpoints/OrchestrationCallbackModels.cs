// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record MessagingSendRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("targetAddress")] string TargetAddress,
    [property: JsonPropertyName("threadId")] Guid ThreadId,
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageContent")] string MessageContent,
    [property: JsonPropertyName("reason")] string? Reason);

// ADR-0049 — `sv.messaging.send` is a one-way delivery tool whose response
// is a delivery acknowledgement: the message was durably placed in the
// recipient's mailbox. It never carries the recipient's response.
public sealed record MessagingSendResponse(
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("threadId")] Guid ThreadId);

public sealed record MessagingBroadcastRequest(
    [property: JsonPropertyName("callerAddress")] string CallerAddress,
    [property: JsonPropertyName("targetAddresses")] string[] TargetAddresses,
    [property: JsonPropertyName("threadId")] Guid ThreadId,
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageContent")] string MessageContent,
    [property: JsonPropertyName("reason")] string? Reason);

// ADR-0049 — `sv.messaging.broadcast` delivers to all targets in parallel
// and reports a per-target delivery outcome (delivered / failed), not the
// recipients' work products.
public sealed record MessagingBroadcastResponse(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("threadId")] Guid ThreadId,
    [property: JsonPropertyName("deliveries")] MessagingDeliveryOutcome[] Deliveries);

public sealed record MessagingDeliveryOutcome(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("error")] string? Error);

public sealed record OrchestrationCallbackErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);

// --- MCP JSON-RPC 2.0 wire shapes ---------------------------------------
//
// The MCP streamable-HTTP transport (the `type: "http"` server the launchers
// write into the agent container's .mcp.json) speaks JSON-RPC 2.0 against the
// messaging callback route-prefix root. The McpRpc* types in
// Cvoya.Spring.Dapr/Mcp/McpJsonRpc.cs are `internal` to Cvoya.Spring.Dapr and
// cannot be reused across the assembly boundary, so the callback host carries
// its own minimal request/response records here.

/// <summary>JSON-RPC 2.0 request envelope for the MCP messaging endpoint.</summary>
public sealed record McpJsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

/// <summary>JSON-RPC 2.0 success response.</summary>
public sealed record McpJsonRpcResponse(
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result")] object Result)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

/// <summary>JSON-RPC 2.0 error response.</summary>
public sealed record McpJsonRpcErrorResponse(
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("error")] McpJsonRpcError Error)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

/// <summary>JSON-RPC 2.0 error body.</summary>
public sealed record McpJsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);
