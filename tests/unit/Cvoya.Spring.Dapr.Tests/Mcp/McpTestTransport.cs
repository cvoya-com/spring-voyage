// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Mcp;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Dapr.Mcp;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Test transport for <see cref="McpServer"/>. ADR-0052 / Wave 3 (#2625)
/// moved the MCP surface from a hand-rolled <c>HttpListener</c> onto a
/// minimal-API route on the worker's Kestrel host. <see cref="McpServer"/>
/// itself is no longer a hosted service and no longer binds a port — the
/// route delegates to <see cref="McpServer.HandleRequestAsync"/>.
/// <para>
/// This helper drives <see cref="McpServer.HandleRequestAsync"/> directly
/// through an in-memory <see cref="DefaultHttpContext"/>, so the JSON-RPC
/// dispatch is exercised exactly as the production route exercises it —
/// without binding a socket or standing up a <c>TestServer</c>.
/// </para>
/// </summary>
internal static class McpTestTransport
{
    /// <summary>
    /// Posts a JSON-RPC request body to <paramref name="server"/> and returns
    /// the HTTP status code plus the parsed JSON response body (or
    /// <c>null</c> when the response carried no body).
    /// </summary>
    public static async Task<(int StatusCode, JsonElement? Body)> PostAsync(
        McpServer server,
        string? token,
        object body,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp/";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        if (token is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {token}";
        }

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await server.HandleRequestAsync(context, cancellationToken);

        if (responseBody.Length == 0)
        {
            return (context.Response.StatusCode, null);
        }

        responseBody.Position = 0;
        using var doc = await JsonDocument.ParseAsync(responseBody, cancellationToken: cancellationToken);
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }

    /// <summary>
    /// Posts a JSON-RPC request and returns the parsed response body,
    /// asserting that the transport returned <c>200 OK</c> (the JSON-RPC
    /// convention — application-level errors ride inside a 200 body).
    /// </summary>
    public static async Task<JsonElement> PostJsonAsync(
        McpServer server,
        string token,
        object body,
        CancellationToken cancellationToken = default)
    {
        var (statusCode, json) = await PostAsync(server, token, body, cancellationToken);
        if (statusCode != StatusCodes.Status200OK)
        {
            throw new InvalidOperationException(
                $"Expected 200 OK from the MCP transport but received {statusCode}.");
        }

        return json ?? throw new InvalidOperationException(
            "Expected a JSON-RPC response body but the MCP transport returned none.");
    }
}
