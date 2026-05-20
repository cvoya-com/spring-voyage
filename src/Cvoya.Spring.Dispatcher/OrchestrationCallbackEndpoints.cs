// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
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

        group.MapPost("/list-members", ListMembersAsync);
        group.MapPost("/inspect", InspectAsync);
        group.MapPost("/delegate-to", DelegateToAsync);
        group.MapPost("/fanout-to", FanoutToAsync);
        group.MapPost("/query-status", QueryStatusAsync);

        return endpoints;
    }

    internal static async Task<IResult> ListMembersAsync(
        [FromBody] ListMembersRequest request,
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

        try
        {
            var members = await handlers.HandleListMembersAsync(
                claims.AgentAddress,
                claims.TenantId,
                claims.ThreadId,
                cancellationToken);

            return Results.Ok(new ListMembersResponse(
                members.Select(member => new OrchestrationMemberDescriptorPayload(
                    member.Address.ToString(),
                    member.DisplayName,
                    member.Kind,
                    member.ExecutionConfig)).ToArray()));
        }
        catch (OrchestrationException ex)
        {
            return MapOrchestrationException(ex);
        }
    }

    internal static async Task<IResult> InspectAsync(
        [FromBody] InspectRequest request,
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

        try
        {
            var metadata = await handlers.HandleInspectAsync(
                claims.AgentAddress,
                claims.TenantId,
                target,
                claims.ThreadId,
                cancellationToken);

            return Results.Ok(new InspectResponse(metadata));
        }
        catch (OrchestrationException ex)
        {
            return MapOrchestrationException(ex);
        }
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

    internal static async Task<IResult> QueryStatusAsync(
        [FromBody] QueryStatusRequest request,
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

        try
        {
            var status = await handlers.HandleQueryStatusAsync(
                claims.AgentAddress,
                claims.TenantId,
                target,
                claims.ThreadId,
                cancellationToken);

            return Results.Ok(new QueryStatusResponse(
                status.Status,
                status.LastActivityAt,
                status.BusyOnThread));
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
