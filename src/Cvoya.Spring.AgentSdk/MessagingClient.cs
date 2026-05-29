// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// <see cref="IMessagingClient"/> implementation that delivers messages
/// through the single platform MCP server (ADR-0051). It calls
/// <c>sv.messaging.send</c> / <c>sv.messaging.multicast</c> over JSON-RPC 2.0
/// <c>tools/call</c>, authenticated by the MCP session bearer token — the
/// same server and credential the runtime already uses for every other
/// <c>sv.*</c> tool.
/// </summary>
/// <remarks>
/// The per-turn callback JWT and the standalone messaging REST surface are
/// retired (ADR-0051). The public <see cref="SendAsync"/> /
/// <see cref="MulticastAsync"/> contract is unchanged — only the transport
/// and the credential moved. The MCP session token carries the caller's
/// identity, thread, and inbound message id, so the SDK no longer threads a
/// caller address or message id on the wire.
/// </remarks>
public sealed class MessagingClient : IMessagingClient
{
    private const string SendTool = "sv.messaging.send";
    private const string MulticastTool = "sv.messaging.multicast";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _mcpToken;
    private int _requestId;

    /// <summary>
    /// Builds the client against the platform MCP endpoint.
    /// </summary>
    /// <param name="mcpUrl">
    /// The MCP server URL (<c>SPRING_MCP_URL</c>) — the same endpoint the
    /// runtime receives for every other <c>sv.*</c> tool.
    /// </param>
    /// <param name="mcpToken">
    /// The MCP session bearer token (<c>SPRING_MCP_TOKEN</c>).
    /// </param>
    public MessagingClient(string mcpUrl, string mcpToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpToken);

        _httpClient = new HttpClient
        {
            BaseAddress = BuildMcpBaseUri(mcpUrl),
        };
        _mcpToken = mcpToken;
    }

    /// <summary>
    /// Test seam: builds the client over a caller-supplied
    /// <see cref="HttpClient"/> (e.g. one wired to a stub
    /// <see cref="HttpMessageHandler"/>) so the JSON-RPC wire shape can be
    /// asserted without a live MCP server.
    /// </summary>
    internal MessagingClient(HttpClient httpClient, string mcpToken)
    {
        _httpClient = httpClient;
        _mcpToken = mcpToken;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ADR-0051 retired the messaging REST surface that backed the result
    /// post; the runtime's final reply now flows through its A2A response.
    /// This method is retained on the contract for source compatibility.
    /// </remarks>
    public Task PostResultAsync(
        string threadId,
        string result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(result);

        throw new NotSupportedException(
            "PostResultAsync is not available over the MCP messaging transport (ADR-0051). " +
            "A runtime's final reply is carried by its A2A response; use sv.messaging.send " +
            "to deliver an explicit message to another participant.");
    }

    /// <inheritdoc />
    public async Task<MessageSendResponse> SendAsync(
        string threadId,
        string targetUnitId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUnitId);
        ArgumentNullException.ThrowIfNull(prompt);

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // #2747 / #2889 — the tool contract takes `recipients[]`, not the
            // pre-#2747 singular `address`. A single-recipient send is a
            // one-element list; the platform adds the caller to the set.
            ["recipients"] = new[] { BuildTargetAddress(targetUnitId) },
            ["message"] = prompt,
        };

        var toolResult = await CallToolAsync(SendTool, arguments, cancellationToken)
            .ConfigureAwait(false);

        var ack = Deserialize<MessagingSendResponseDto>(toolResult);
        return new MessageSendResponse(
            ack.Delivered,
            ack.MessageId ?? string.Empty,
            ack.Target ?? string.Empty,
            ack.ThreadId ?? string.Empty);
    }

    /// <inheritdoc />
    public async Task<MessageMulticastResponse> MulticastAsync(
        string threadId,
        IReadOnlyList<string> targetUnitIds,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(targetUnitIds);
        ArgumentNullException.ThrowIfNull(prompt);

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // #2747 / #2889 — `recipients[]` is the current contract field for
            // both send and multicast (the pre-#2747 `addresses` is gone).
            ["recipients"] = targetUnitIds.Select(BuildTargetAddress).ToArray(),
            ["message"] = prompt,
        };

        var toolResult = await CallToolAsync(MulticastTool, arguments, cancellationToken)
            .ConfigureAwait(false);

        var result = Deserialize<MessagingMulticastResponseDto>(toolResult);
        return new MessageMulticastResponse(
            result.MessageId ?? string.Empty,
            result.ThreadId ?? string.Empty,
            (result.Deliveries ?? Array.Empty<MessagingDeliveryOutcomeDto>())
                .Select(outcome => new MessageMulticastDelivery(
                    outcome.Target ?? string.Empty,
                    outcome.Delivered,
                    outcome.Error))
                .ToArray());
    }

    private static Uri BuildMcpBaseUri(string mcpUrl)
    {
        if (!Uri.TryCreate(mcpUrl, UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "MCP URL must be an absolute http(s) URL.",
                nameof(mcpUrl));
        }

        return baseUri;
    }

    private static string BuildTargetAddress(string targetUnitId)
    {
        if (targetUnitId.Contains(':', StringComparison.Ordinal))
        {
            return targetUnitId;
        }

        return Guid.TryParse(targetUnitId, out var targetId)
            ? $"unit:{targetId:N}"
            : targetUnitId;
    }

    /// <summary>
    /// Issues an MCP <c>tools/call</c> JSON-RPC request and returns the
    /// parsed text content. Surfaces an MCP <c>isError</c> tool result or a
    /// JSON-RPC error as an <see cref="MessageDeliveryException"/>-shaped
    /// failure so callers see a uniform contract.
    /// </summary>
    private async Task<JsonElement> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var rpcRequest = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments,
            },
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(rpcRequest, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mcpToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MessagingTransportException(
                "Failed to call the Spring Voyage platform MCP server.",
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MessagingTransportException(
                "Timed out calling the Spring Voyage platform MCP server.",
                ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new MessagingAuthException(
                    $"Spring Voyage MCP server rejected the session token (HTTP {(int)response.StatusCode}).",
                    "InvalidToken");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MessagingTransportException(
                    $"Spring Voyage MCP server returned HTTP {(int)response.StatusCode}.");
            }

            return ParseToolResult(toolName, body);
        }
    }

    /// <summary>
    /// Parses an MCP JSON-RPC response, unwrapping the <c>tools/call</c>
    /// result envelope (<c>{ content: [{ type, text }], isError }</c>). A
    /// transport-level JSON-RPC <c>error</c>, an <c>isError: true</c> tool
    /// result, or an unauthenticated rejection raises the matching
    /// message-delivery exception.
    /// </summary>
    private static JsonElement ParseToolResult(string toolName, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new MessagingTransportException(
                "Spring Voyage MCP server returned an invalid JSON-RPC response body.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.String
                        ? msg.GetString()
                        : null;
                throw new MessagingTransportException(
                    $"Spring Voyage MCP server rejected '{toolName}': {message ?? "JSON-RPC error."}");
            }

            if (!root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object)
            {
                throw new MessagingTransportException(
                    $"Spring Voyage MCP server returned no result for '{toolName}'.");
            }

            var isError = result.TryGetProperty("isError", out var isErrorProp) &&
                isErrorProp.ValueKind == JsonValueKind.True;

            var text = ExtractContentText(result);

            if (isError)
            {
                throw new MessagingTransportException(
                    $"Spring Voyage messaging tool '{toolName}' failed: {text}");
            }

            try
            {
                using var inner = JsonDocument.Parse(text);
                return inner.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new MessagingTransportException(
                    $"Spring Voyage messaging tool '{toolName}' returned a non-JSON result body.",
                    ex);
            }
        }
    }

    private static string ExtractContentText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("text", out var textProp) &&
                    textProp.ValueKind == JsonValueKind.String)
                {
                    return textProp.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private static T Deserialize<T>(JsonElement element)
    {
        var value = element.Deserialize<T>(JsonOptions);
        return value
            ?? throw new MessagingTransportException(
                "Spring Voyage messaging tool returned an empty result body.");
    }

    // ADR-0049 — the platform returns a delivery acknowledgement, not the
    // recipient's response. The wire shape mirrors SvMessagingSkillRegistry's
    // tool output (string ids, ADR-0051).
    private sealed record MessagingSendResponseDto(
        bool Delivered,
        string? MessageId,
        string? Target,
        string? ThreadId);

    private sealed record MessagingMulticastResponseDto(
        string? MessageId,
        string? ThreadId,
        MessagingDeliveryOutcomeDto[]? Deliveries);

    private sealed record MessagingDeliveryOutcomeDto(
        string? Target,
        bool Delivered,
        string? Error);
}
