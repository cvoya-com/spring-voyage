// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dispatcher.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

public static class OrchestrationCallbackEndpoints
{
    public const string RoutePrefix = AgentCallbackEnvironmentContract.OrchestrationRoutePrefix;

    public static IEndpointRouteBuilder MapOrchestrationCallbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(RoutePrefix);

        group.MapPost("/delegate-to", DelegateToAsync);
        group.MapPost("/fanout-to", FanoutToAsync);

        // MCP streamable-HTTP transport. The claude-code / codex launchers
        // write a `spring-orchestration` server (type: "http") into the agent
        // container's .mcp.json, pointed at the orchestration route-prefix
        // ROOT. The CLI POSTs JSON-RPC 2.0 (`initialize`, `tools/list`,
        // `tools/call`) there — without this handler the MCP handshake 404s
        // and the CLI silently drops the server. An in-group MapPost("")
        // pattern does not match the bare prefix, so the root handler is
        // mapped directly on `endpoints`. ASP.NET Core route matching
        // normalises a trailing slash, so this single route matches both the
        // launcher's bare-prefix url and the `…/` form. The two REST
        // sub-routes above stay as-is — they serve OrchestrationClient.
        endpoints.MapPost(RoutePrefix, McpRpcAsync);

        return endpoints;
    }

    internal static async Task<IResult> McpRpcAsync(
        [FromBody] McpJsonRpcRequest? request,
        CallbackTokenValidator tokenValidator,
        OrchestrationToolHandlers handlers,
        IOrchestrationToolProvider toolProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var requestId = request?.Id;

        if (!TryValidateMcpCallback(httpContext, tokenValidator, out var claims, out var authError))
        {
            return McpError(StatusCodes.Status401Unauthorized, requestId, McpErrorCodes.Unauthorized, authError);
        }

        if (request is null || string.IsNullOrEmpty(request.Method))
        {
            return McpError(
                StatusCodes.Status200OK, requestId, McpErrorCodes.InvalidRequest,
                "Empty or malformed JSON-RPC request.");
        }

        switch (request.Method)
        {
            case "initialize":
                return McpResult(requestId, BuildInitializeResult());

            case "tools/list":
                return McpResult(requestId, BuildToolListResult(toolProvider, claims));

            case "tools/call":
                return await HandleMcpToolCallAsync(request, requestId, claims, handlers, cancellationToken);

            default:
                return McpError(
                    StatusCodes.Status200OK, requestId, McpErrorCodes.MethodNotFound,
                    $"Method '{request.Method}' is not supported.");
        }
    }

    private static object BuildInitializeResult() => new
    {
        protocolVersion = "2024-11-05",
        serverInfo = new { name = "spring-orchestration", version = "1.0.0" },
        capabilities = new { tools = new { } },
    };

    private static object BuildToolListResult(
        IOrchestrationToolProvider toolProvider,
        CallbackToken claims)
    {
        var descriptors = toolProvider.GetOrchestrationTools(claims.AgentAddress, claims.ThreadId);

        var tools = descriptors
            .Select(descriptor => new
            {
                name = ToWireName(descriptor.Name),
                description = ExtractSchemaDescription(descriptor.InputSchema),
                inputSchema = descriptor.InputSchema,
            })
            .ToArray();

        return new { tools };
    }

    private static async Task<IResult> HandleMcpToolCallAsync(
        McpJsonRpcRequest request,
        JsonElement? requestId,
        CallbackToken claims,
        OrchestrationToolHandlers handlers,
        CancellationToken cancellationToken)
    {
        if (request.Params is not { ValueKind: JsonValueKind.Object } paramsElement)
        {
            return McpError(
                StatusCodes.Status200OK, requestId, McpErrorCodes.InvalidParams,
                "tools/call requires a params object.");
        }

        if (!paramsElement.TryGetProperty("name", out var nameProp) ||
            nameProp.ValueKind != JsonValueKind.String)
        {
            return McpError(
                StatusCodes.Status200OK, requestId, McpErrorCodes.InvalidParams,
                "tools/call requires a 'name' string.");
        }

        var toolName = nameProp.GetString()!;
        var arguments = paramsElement.TryGetProperty("arguments", out var argsProp) &&
                        argsProp.ValueKind == JsonValueKind.Object
            ? argsProp
            : default;

        switch (toolName)
        {
            case "delegate_to":
                return await HandleMcpDelegateToAsync(requestId, arguments, claims, handlers, cancellationToken);

            case "fanout_to":
                return await HandleMcpFanoutToAsync(requestId, arguments, claims, handlers, cancellationToken);

            default:
                return McpError(
                    StatusCodes.Status200OK, requestId, McpErrorCodes.MethodNotFound,
                    $"Tool '{toolName}' is not an orchestration tool.");
        }
    }

    private static async Task<IResult> HandleMcpDelegateToAsync(
        JsonElement? requestId,
        JsonElement arguments,
        CallbackToken claims,
        OrchestrationToolHandlers handlers,
        CancellationToken cancellationToken)
    {
        if (!TryGetStringArgument(arguments, "address", out var addressValue))
        {
            return McpToolError(requestId, "delegate_to requires an 'address' string argument.");
        }

        if (!Address.TryParse(addressValue, out var target) || target is null)
        {
            return McpToolError(requestId, $"'{addressValue}' is not a valid Spring Voyage address.");
        }

        var reason = TryGetStringArgument(arguments, "reason", out var reasonValue) ? reasonValue : null;
        var message = BuildMcpMessage(claims, target, ExtractMessagePayload(arguments));

        try
        {
            var response = await handlers.HandleDelegateToAsync(
                claims.AgentAddress,
                claims.TenantId,
                target,
                message,
                reason,
                claims.ThreadId,
                cancellationToken);

            return McpToolResult(requestId, ToCallbackMessage(response), isError: false);
        }
        catch (OrchestrationException ex)
        {
            return McpOrchestrationToolError(requestId, ex);
        }
    }

    private static async Task<IResult> HandleMcpFanoutToAsync(
        JsonElement? requestId,
        JsonElement arguments,
        CallbackToken claims,
        OrchestrationToolHandlers handlers,
        CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("addresses", out var addressesProp) ||
            addressesProp.ValueKind != JsonValueKind.Array)
        {
            return McpToolError(requestId, "fanout_to requires an 'addresses' array argument.");
        }

        var targets = new List<Address>();
        foreach (var element in addressesProp.EnumerateArray())
        {
            var addressValue = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (!Address.TryParse(addressValue, out var target) || target is null)
            {
                return McpToolError(requestId, $"'{addressValue}' is not a valid Spring Voyage address.");
            }

            targets.Add(target);
        }

        var reason = TryGetStringArgument(arguments, "reason", out var reasonValue) ? reasonValue : null;
        var message = BuildMcpMessage(
            claims,
            targets.Count > 0 ? targets[0] : claims.AgentAddress,
            ExtractMessagePayload(arguments));

        try
        {
            var results = await handlers.HandleFanoutToAsync(
                claims.AgentAddress,
                claims.TenantId,
                targets,
                message,
                reason,
                claims.ThreadId,
                cancellationToken);

            var mapped = results
                .Select(result => new FanoutTargetResult(
                    result.Target.ToString(),
                    result.Error is null,
                    result.Error?.Message,
                    ToCallbackMessage(result.Response)))
                .ToArray();

            return McpToolResult(requestId, mapped, isError: false);
        }
        catch (OrchestrationException ex)
        {
            return McpOrchestrationToolError(requestId, ex);
        }
    }

    private static bool TryValidateMcpCallback(
        HttpContext httpContext,
        CallbackTokenValidator tokenValidator,
        out CallbackToken claims,
        out string error)
    {
        claims = default!;
        error = string.Empty;

        if (!TryExtractBearerToken(httpContext, out var token))
        {
            error = "Authorization header must contain a bearer callback token.";
            return false;
        }

        try
        {
            claims = tokenValidator.Validate(token);
        }
        catch (CallbackTokenValidationException ex)
        {
            error = ex.Message;
            return false;
        }

        if (!IsSupportedCallerScheme(claims.AgentAddress))
        {
            error =
                $"Orchestration callbacks support unit:// and agent:// callers; got '{claims.AgentAddress}'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds an orchestration <see cref="Message"/> from a model-supplied
    /// tool-call argument. The <c>message</c> argument is opaque per the
    /// embedded input schema (<c>description</c>-only, no <c>type</c>): a JSON
    /// string is wrapped as <c>{ content: &lt;string&gt; }</c> (mirroring the
    /// REST <see cref="BuildMessage"/>); a JSON object is passed through; any
    /// other shape (or a missing argument) yields an empty object payload.
    /// </summary>
    private static Message BuildMcpMessage(CallbackToken claims, Address target, JsonElement payload)
    {
        return new Message(
            claims.MessageId,
            claims.AgentAddress,
            target,
            MessageType.Domain,
            GuidFormatter.Format(claims.ThreadId),
            payload,
            DateTimeOffset.UtcNow);
    }

    private static JsonElement ExtractMessagePayload(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("message", out var message))
        {
            return message.ValueKind switch
            {
                JsonValueKind.String =>
                    JsonSerializer.SerializeToElement(new { content = message.GetString() }),
                JsonValueKind.Object => message,
                _ => JsonSerializer.SerializeToElement(new { }),
            };
        }

        return JsonSerializer.SerializeToElement(new { });
    }

    private static bool TryGetStringArgument(JsonElement arguments, string name, out string? value)
    {
        value = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return value is not null;
        }

        return false;
    }

    private static string ExtractSchemaDescription(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind == JsonValueKind.Object &&
            inputSchema.TryGetProperty("description", out var description) &&
            description.ValueKind == JsonValueKind.String)
        {
            return description.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ToWireName(OrchestrationToolName name) => name switch
    {
        OrchestrationToolName.DelegateTo => "delegate_to",
        OrchestrationToolName.FanoutTo => "fanout_to",
        _ => name.ToString(),
    };

    private static IResult McpResult(JsonElement? id, object result) =>
        Results.Json(new McpJsonRpcResponse(id, result), statusCode: StatusCodes.Status200OK);

    private static IResult McpError(int statusCode, JsonElement? id, int code, string message) =>
        Results.Json(
            new McpJsonRpcErrorResponse(id, new McpJsonRpcError(code, message)),
            statusCode: statusCode);

    /// <summary>
    /// Wraps an MCP <c>tools/call</c> outcome in the standard result envelope
    /// (<c>{ content: [{ type: "text", text: &lt;json&gt; }], isError }</c>).
    /// The text block carries the JSON the model sees — serialized so it
    /// matches the REST response shapes (<see cref="OrchestrationCallbackMessage"/>
    /// for delegate, <see cref="FanoutTargetResult"/><c>[]</c> for fanout).
    /// </summary>
    private static IResult McpToolResult(JsonElement? id, object? payload, bool isError)
    {
        var text = JsonSerializer.Serialize(payload);
        return McpResult(id, new
        {
            content = new[] { new { type = "text", text } },
            isError,
        });
    }

    private static IResult McpToolError(JsonElement? id, string message) =>
        McpResult(id, new
        {
            content = new[] { new { type = "text", text = message } },
            isError = true,
        });

    private static IResult McpOrchestrationToolError(JsonElement? id, OrchestrationException ex) =>
        McpToolError(id, $"{ex.RejectCode}: {ex.Message}");

    private static class McpErrorCodes
    {
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int Unauthorized = -32001;
    }

    internal static async Task<IResult> DelegateToAsync(
        [FromBody] DelegateToRequest request,
        CallbackTokenValidator tokenValidator,
        OrchestrationToolHandlers handlers,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCallback(httpContext, tokenValidator, out var claims, out var error) ||
            !TryValidateRequestScope(request.CallerAddress, request.ThreadId, claims, out error) ||
            !TryParseAddress(request.TargetAddress, "TargetAddress", out var target, out error))
        {
            return error;
        }

        var message = BuildMessage(claims, target, request.MessageId, request.MessageContent);

        try
        {
            var response = await handlers.HandleDelegateToAsync(
                claims.AgentAddress,
                claims.TenantId,
                target,
                message,
                request.Reason,
                claims.ThreadId,
                cancellationToken);

            return Results.Ok(new DelegateToResponse(ToCallbackMessage(response)));
        }
        catch (OrchestrationException ex)
        {
            return MapOrchestrationException(ex);
        }
    }

    internal static async Task<IResult> FanoutToAsync(
        [FromBody] FanoutToRequest request,
        CallbackTokenValidator tokenValidator,
        OrchestrationToolHandlers handlers,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCallback(httpContext, tokenValidator, out var claims, out var error) ||
            !TryValidateRequestScope(request.CallerAddress, request.ThreadId, claims, out error))
        {
            return error;
        }

        if (request.TargetAddresses is null)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "InvalidAddress",
                "TargetAddresses is required.");
        }

        var targets = new List<Address>(request.TargetAddresses.Length);
        foreach (var targetAddress in request.TargetAddresses)
        {
            if (!TryParseAddress(targetAddress, "TargetAddresses", out var target, out error))
            {
                return error;
            }

            targets.Add(target);
        }

        var message = BuildMessage(claims, targets.Count > 0 ? targets[0] : claims.AgentAddress, request.MessageId, request.MessageContent);

        try
        {
            var results = await handlers.HandleFanoutToAsync(
                claims.AgentAddress,
                claims.TenantId,
                targets,
                message,
                request.Reason,
                claims.ThreadId,
                cancellationToken);

            return Results.Ok(new FanoutToResponse(
                results.Select(result => new FanoutTargetResult(
                    result.Target.ToString(),
                    result.Error is null,
                    result.Error?.Message,
                    ToCallbackMessage(result.Response))).ToArray()));
        }
        catch (OrchestrationException ex)
        {
            return MapOrchestrationException(ex);
        }
    }

    private static bool TryValidateCallback(
        HttpContext httpContext,
        CallbackTokenValidator tokenValidator,
        out CallbackToken claims,
        out IResult error)
    {
        claims = default!;
        error = Results.Empty;

        if (!TryExtractBearerToken(httpContext, out var token))
        {
            error = Error(
                StatusCodes.Status401Unauthorized,
                "InvalidToken",
                "Authorization header must contain a bearer callback token.");
            return false;
        }

        try
        {
            claims = tokenValidator.Validate(token);
        }
        catch (CallbackTokenValidationException ex)
        {
            error = Error(
                StatusCodes.Status401Unauthorized,
                "InvalidToken",
                ex.Message);
            return false;
        }

        if (!IsSupportedCallerScheme(claims.AgentAddress))
        {
            error = Error(
                StatusCodes.Status403Forbidden,
                "UnsupportedCallerScheme",
                $"Orchestration callbacks support unit:// and agent:// callers; got '{claims.AgentAddress}'.");
            return false;
        }

        // Cross-tenant containment (ADR-0039 §3 gate 6) is enforced inside
        // each handler via IOrchestrationTenantResolver — the per-tenant
        // signing key from D12 makes a forged token for another tenant
        // structurally implausible, but the handler-side gate is explicit so
        // any future authentication shape (mTLS, OIDC) inherits the same
        // containment without re-deriving it from the signing-key story.
        return true;
    }

    private static bool TryValidateRequestScope(
        string callerAddress,
        Guid threadId,
        CallbackToken claims,
        out IResult error)
    {
        error = Results.Empty;

        if (!TryParseAddress(callerAddress, "CallerAddress", out var requestCaller, out error))
        {
            return false;
        }

        if (!AddressEquals(requestCaller, claims.AgentAddress))
        {
            error = Error(
                StatusCodes.Status403Forbidden,
                "CallerMismatch",
                "Request callerAddress must match the callback token agent address.");
            return false;
        }

        if (threadId != claims.ThreadId)
        {
            error = Error(
                StatusCodes.Status400BadRequest,
                "ThreadMismatch",
                "Request threadId must match the callback token thread id.");
            return false;
        }

        return true;
    }

    private static bool TryExtractBearerToken(HttpContext httpContext, out string token)
    {
        token = string.Empty;
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            return false;
        }

        var headerValue = authHeader.ToString();
        const string bearerPrefix = "Bearer ";
        if (!headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = headerValue[bearerPrefix.Length..].Trim();
        return token.Length > 0;
    }

    private static bool TryParseAddress(
        string? value,
        string fieldName,
        out Address address,
        out IResult error)
    {
        address = default!;
        error = Results.Empty;

        if (!Address.TryParse(value, out var parsed) || parsed is null)
        {
            error = Error(
                StatusCodes.Status400BadRequest,
                "InvalidAddress",
                $"{fieldName} must be a valid Spring Voyage address.");
            return false;
        }

        address = parsed;
        return true;
    }

    private static Message BuildMessage(
        CallbackToken claims,
        Address target,
        Guid requestMessageId,
        string messageContent)
    {
        var payload = JsonSerializer.SerializeToElement(new { content = messageContent });
        var messageId = requestMessageId == Guid.Empty ? claims.MessageId : requestMessageId;

        return new Message(
            messageId,
            claims.AgentAddress,
            target,
            MessageType.Domain,
            GuidFormatter.Format(claims.ThreadId),
            payload,
            DateTimeOffset.UtcNow);
    }

    private static OrchestrationCallbackMessage? ToCallbackMessage(Message? message)
    {
        if (message is null)
        {
            return null;
        }

        return new OrchestrationCallbackMessage(
            message.Id,
            message.From.ToString(),
            message.To.ToString(),
            message.ThreadId,
            ExtractMessageContent(message.Payload));
    }

    private static string ExtractMessageContent(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (payload.ValueKind == JsonValueKind.String)
        {
            return payload.GetString() ?? string.Empty;
        }

        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in payload.EnumerateObject())
            {
                if (string.Equals(property.Name, "content", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? string.Empty;
                }
            }
        }

        return payload.GetRawText();
    }

    private static IResult MapOrchestrationException(OrchestrationException ex)
    {
        var statusCode = ex.RejectCode switch
        {
            OrchestrationException.RejectCodes.OrchestrationSelfDelegation =>
                StatusCodes.Status400BadRequest,
            OrchestrationException.RejectCodes.OrchestrationDepthExceeded =>
                StatusCodes.Status429TooManyRequests,
            // ADR-0039 §3 gate 6 — cross-tenant containment maps to 403,
            // matching every other "you are not authorised on this surface"
            // gate the dispatcher applies. The SDK already maps this code
            // onto OrchestrationAuthException(Reason="CrossTenant").
            OrchestrationException.RejectCodes.OrchestrationCrossTenant =>
                StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };

        return Error(statusCode, ex.RejectCode, ex.Message);
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new OrchestrationCallbackErrorResponse(code, message), statusCode: statusCode);

    private static bool IsSupportedCallerScheme(Address address) =>
        string.Equals(address.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(address.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase);

    private static bool AddressEquals(Address left, Address right) =>
        left.Id == right.Id &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase);
}
