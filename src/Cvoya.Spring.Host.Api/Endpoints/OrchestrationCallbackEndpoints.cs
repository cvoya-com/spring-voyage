// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

public static class OrchestrationCallbackEndpoints
{
    public const string RoutePrefix = AgentCallbackEnvironmentContract.OrchestrationRoutePrefix;

    public static IEndpointRouteBuilder MapOrchestrationCallbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The orchestration callback surface is an internal agent-runtime
        // ingress, not part of the tenant REST API the OpenAPI document
        // describes for SDK / portal consumers. It authenticates via the
        // per-invocation callback JWT, not the API-token scheme, and is
        // reached only from runtime containers. Exclude it from the OpenAPI
        // description so the public contract stays scoped to the tenant API
        // — matching how /health is excluded.
        var group = endpoints.MapGroup(RoutePrefix).ExcludeFromDescription();

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
        endpoints.MapPost(RoutePrefix, McpRpcAsync).ExcludeFromDescription();

        return endpoints;
    }

    internal static async Task<IResult> McpRpcAsync(
        [FromBody] McpJsonRpcRequest? request,
        CallbackTokenValidator tokenValidator,
        OrchestrationToolHandlers handlers,
        IOrchestrationToolProvider toolProvider,
        OrchestrationCallbackDiagnostics diagnostics,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var requestId = request?.Id;

        var mcpAuth = await TryValidateMcpCallbackAsync(
            httpContext, tokenValidator, diagnostics, cancellationToken);
        if (!mcpAuth.Succeeded)
        {
            return McpError(
                StatusCodes.Status401Unauthorized, requestId, McpErrorCodes.Unauthorized, mcpAuth.Error);
        }

        var claims = mcpAuth.Claims;

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
            var ack = await handlers.HandleDelegateToAsync(
                claims.AgentAddress,
                claims.TenantId,
                target,
                message,
                reason,
                claims.ThreadId,
                cancellationToken);

            return McpToolResult(requestId, ToDelegateAck(ack), isError: false);
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
            var outcomes = await handlers.HandleFanoutToAsync(
                claims.AgentAddress,
                claims.TenantId,
                targets,
                message,
                reason,
                claims.ThreadId,
                cancellationToken);

            // ADR-0049 §6 — a fanout where every delivery failed is itself a
            // terminal failure; surface it to the model with isError: true.
            var anyDelivered = outcomes.Any(outcome => outcome.Delivered);
            return McpToolResult(
                requestId,
                ToFanoutAck(message.Id, claims.ThreadId, outcomes),
                isError: outcomes.Count > 0 && !anyDelivered);
        }
        catch (OrchestrationException ex)
        {
            return McpOrchestrationToolError(requestId, ex);
        }
    }

    /// <summary>
    /// Outcome of an orchestration callback-token validation attempt.
    /// </summary>
    private readonly record struct CallbackAuthResult(
        bool Succeeded,
        CallbackToken Claims,
        string Error);

    private static async Task<CallbackAuthResult> TryValidateMcpCallbackAsync(
        HttpContext httpContext,
        CallbackTokenValidator tokenValidator,
        OrchestrationCallbackDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (!TryExtractBearerToken(httpContext, out var token))
        {
            return new CallbackAuthResult(
                false, default!, "Authorization header must contain a bearer callback token.");
        }

        CallbackToken claims;
        try
        {
            claims = tokenValidator.Validate(token);
        }
        catch (CallbackTokenValidationException ex)
        {
            // #2582: surface the rejection as a warning + ErrorOccurred
            // activity instead of letting a 401 pass silently.
            await diagnostics.RecordRejectionAsync(ex, token, cancellationToken);
            return new CallbackAuthResult(false, default!, ex.Message);
        }

        if (!IsSupportedCallerScheme(claims.AgentAddress))
        {
            return new CallbackAuthResult(
                false,
                default!,
                $"Orchestration callbacks support unit:// and agent:// callers; got '{claims.AgentAddress}'.");
        }

        return new CallbackAuthResult(true, claims, string.Empty);
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
    /// The text block carries the JSON the model sees — the same delivery
    /// acknowledgement the REST sub-routes return (<see cref="DelegateToResponse"/>
    /// for delegate, <see cref="FanoutToResponse"/> for fanout — ADR-0049).
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
        OrchestrationCallbackDiagnostics diagnostics,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var (ok, claims, error) = await TryValidateCallbackAsync(
            httpContext, tokenValidator, diagnostics, cancellationToken);
        if (!ok ||
            !TryValidateRequestScope(request.CallerAddress, request.ThreadId, claims, out error) ||
            !TryParseAddress(request.TargetAddress, "TargetAddress", out var target, out error))
        {
            return error;
        }

        var message = BuildMessage(claims, target, request.MessageId, request.MessageContent);

        try
        {
            var ack = await handlers.HandleDelegateToAsync(
                claims.AgentAddress,
                claims.TenantId,
                target,
                message,
                request.Reason,
                claims.ThreadId,
                cancellationToken);

            return Results.Ok(ToDelegateAck(ack));
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
        OrchestrationCallbackDiagnostics diagnostics,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var (ok, claims, error) = await TryValidateCallbackAsync(
            httpContext, tokenValidator, diagnostics, cancellationToken);
        if (!ok ||
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
            var outcomes = await handlers.HandleFanoutToAsync(
                claims.AgentAddress,
                claims.TenantId,
                targets,
                message,
                request.Reason,
                claims.ThreadId,
                cancellationToken);

            return Results.Ok(ToFanoutAck(message.Id, claims.ThreadId, outcomes));
        }
        catch (OrchestrationException ex)
        {
            return MapOrchestrationException(ex);
        }
    }

    // ADR-0049 — REST + MCP both serialise the handler's delivery
    // acknowledgements through these mappers, so the SDK and the MCP
    // transport see one wire contract.
    private static DelegateToResponse ToDelegateAck(DelegateDeliveryAck ack) =>
        new(ack.Delivered, ack.MessageId, ack.Target.ToString(), ack.ThreadId);

    private static FanoutToResponse ToFanoutAck(
        Guid messageId,
        Guid threadId,
        IReadOnlyList<FanoutDeliveryAck> outcomes) =>
        new(
            messageId,
            threadId,
            outcomes
                .Select(outcome => new FanoutDeliveryOutcome(
                    outcome.Target.ToString(),
                    outcome.Delivered,
                    outcome.Error))
                .ToArray());

    private static async Task<(bool Ok, CallbackToken Claims, IResult Error)> TryValidateCallbackAsync(
        HttpContext httpContext,
        CallbackTokenValidator tokenValidator,
        OrchestrationCallbackDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (!TryExtractBearerToken(httpContext, out var token))
        {
            return (
                false,
                default!,
                Error(
                    StatusCodes.Status401Unauthorized,
                    "InvalidToken",
                    "Authorization header must contain a bearer callback token."));
        }

        CallbackToken claims;
        try
        {
            claims = tokenValidator.Validate(token);
        }
        catch (CallbackTokenValidationException ex)
        {
            // #2582: surface the rejection as a warning + ErrorOccurred
            // activity instead of letting a 401 pass silently.
            await diagnostics.RecordRejectionAsync(ex, token, cancellationToken);
            return (
                false,
                default!,
                Error(StatusCodes.Status401Unauthorized, "InvalidToken", ex.Message));
        }

        if (!IsSupportedCallerScheme(claims.AgentAddress))
        {
            return (
                false,
                default!,
                Error(
                    StatusCodes.Status403Forbidden,
                    "UnsupportedCallerScheme",
                    $"Orchestration callbacks support unit:// and agent:// callers; got '{claims.AgentAddress}'."));
        }

        // Cross-tenant containment (ADR-0039 §3 gate 6) is enforced inside
        // each handler via IOrchestrationTenantResolver — the per-tenant
        // signing key from D12 makes a forged token for another tenant
        // structurally implausible, but the handler-side gate is explicit so
        // any future authentication shape (mTLS, OIDC) inherits the same
        // containment without re-deriving it from the signing-key story.
        return (true, claims, Results.Empty);
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

    private static IResult MapOrchestrationException(OrchestrationException ex)
    {
        var statusCode = ex.RejectCode switch
        {
            OrchestrationException.RejectCodes.OrchestrationSelfDelegation =>
                StatusCodes.Status400BadRequest,
            OrchestrationException.RejectCodes.OrchestrationDepthExceeded =>
                StatusCodes.Status429TooManyRequests,
            // ADR-0049 §6 — terminal delivery failure means the platform is
            // degraded (transient infrastructure persisted past the R/T
            // budget). 503 tells the caller the condition is transient.
            OrchestrationException.RejectCodes.OrchestrationDeliveryFailed =>
                StatusCodes.Status503ServiceUnavailable,
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
