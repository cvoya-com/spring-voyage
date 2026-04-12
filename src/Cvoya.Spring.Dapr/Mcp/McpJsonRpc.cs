// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Mcp;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Minimal JSON-RPC 2.0 request envelope used by the MCP server. Only the fields
/// needed by the subset of MCP we implement (<c>initialize</c>, <c>tools/list</c>,
/// <c>tools/call</c>) are deserialized.
/// </summary>
internal sealed class McpRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 success response.</summary>
internal sealed class McpRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }
}

/// <summary>JSON-RPC 2.0 error response.</summary>
internal sealed class McpRpcErrorResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("error")]
    public McpRpcError Error { get; set; } = new();
}

/// <summary>JSON-RPC 2.0 error body.</summary>
internal sealed class McpRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// Standard JSON-RPC error codes used by the MCP server.
/// </summary>
internal static class McpRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>Custom code for unauthenticated requests (outside standard JSON-RPC range).</summary>
    public const int Unauthorized = -32001;
}