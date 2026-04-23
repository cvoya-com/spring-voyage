// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Maps the OAuth flow endpoints onto the route group passed in by
/// <see cref="GitHubConnectorType.MapRoutes"/>. The routes live under
/// <c>/api/v1/connectors/github/oauth/…</c> because Host.Api scopes the
/// outer prefix for every connector; this file only knows the inner path
/// shape.
/// </summary>
public static class GitHubOAuthEndpoints
{
    /// <summary>
    /// Registers <c>authorize</c>, <c>callback</c>, <c>revoke</c> and
    /// <c>session</c> endpoints on the supplied builder.
    /// </summary>
    public static void MapOAuthEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/oauth/authorize", AuthorizeAsync)
            .WithName("BeginGitHubOAuthAuthorization")
            .WithSummary("Start an OAuth authorization flow and return the GitHub authorize URL")
            .WithTags("Connectors.GitHub.OAuth")
            .Accepts<OAuthAuthorizeRequest>("application/json")
            .Produces<OAuthAuthorizeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/oauth/callback", CallbackAsync)
            .WithName("HandleGitHubOAuthCallback")
            .WithSummary("Consume the OAuth callback: validate state, exchange code, issue a session")
            .WithTags("Connectors.GitHub.OAuth")
            .Produces<OAuthCallbackResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapPost("/oauth/revoke/{sessionId}", RevokeAsync)
            .WithName("RevokeGitHubOAuthSession")
            .WithSummary("Revoke the GitHub grant for the session and delete the local record")
            .WithTags("Connectors.GitHub.OAuth")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/oauth/session/{sessionId}", GetSessionAsync)
            .WithName("GetGitHubOAuthSession")
            .WithSummary("Return session metadata (login, scopes, expires_at) — never the token")
            .WithTags("Connectors.GitHub.OAuth")
            .Produces<OAuthSessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> AuthorizeAsync(
        [FromBody] OAuthAuthorizeRequest? request,
        [FromServices] IGitHubOAuthService service,
        CancellationToken ct)
    {
        try
        {
            var result = await service.BeginAuthorizationAsync(
                scopesOverride: request?.Scopes,
                clientState: request?.ClientState,
                ct);
            return Results.Ok(new OAuthAuthorizeResponse(result.AuthorizeUrl, result.State));
        }
        catch (InvalidOperationException ex)
        {
            // Raised when ClientId / RedirectUri are not configured. Surface
            // as 502 — the server is misconfigured, the caller can't fix it
            // by retrying a different body.
            return Results.Problem(
                title: "GitHub OAuth is not configured",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> CallbackAsync(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        [FromServices] IGitHubOAuthService service,
        CancellationToken ct)
    {
        // GitHub forwards user-initiated failures (e.g. the user declined
        // consent) on the query string rather than a non-2xx. Surface those
        // unchanged so the portal can display GitHub's own wording.
        if (!string.IsNullOrEmpty(error))
        {
            return Results.Problem(
                title: "GitHub rejected the OAuth authorization",
                detail: errorDescription ?? error,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["error"] = error,
                });
        }

        try
        {
            var result = await service.HandleCallbackAsync(code ?? string.Empty, state ?? string.Empty, ct);
            if (result.SessionId is null)
            {
                var status = result.Error switch
                {
                    "invalid_state" or "invalid_request" => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status502BadGateway,
                };
                return Results.Problem(
                    title: "GitHub OAuth callback failed",
                    detail: result.ErrorDescription ?? result.Error,
                    statusCode: status,
                    extensions: new Dictionary<string, object?>
                    {
                        ["error"] = result.Error,
                    });
            }

            // #1153: when the wizard initiated the flow it stamped the
            // ClientState with the portal path it wants the user to
            // return to (e.g. "/units/new?step=github"). Redirect there
            // with the session id in the URL fragment so the wizard can
            // resume — the fragment never reaches the server, so the
            // session id stays out of access logs / referrer headers.
            // Only same-origin redirect targets are honoured (must start
            // with a single "/") to stop arbitrary open-redirect abuse.
            var session = await service.GetSessionAsync(result.SessionId, ct);
            var clientState = session?.ClientState;
            if (IsSafeReturnPath(clientState))
            {
                var separator = clientState!.Contains('#') ? '&' : '#';
                var target = $"{clientState}{separator}oauth_session_id={Uri.EscapeDataString(result.SessionId)}&login={Uri.EscapeDataString(result.Login ?? string.Empty)}";
                return Results.Redirect(target);
            }

            return Results.Ok(new OAuthCallbackResponse(result.SessionId, result.Login!));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "GitHub OAuth is not configured",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a relative same-origin path the
    /// callback is allowed to redirect to. Restricts the target to a single
    /// leading <c>/</c> followed by a non-<c>/</c> character so common
    /// open-redirect tricks (<c>//evil.com</c>, <c>/\evil.com</c>) and
    /// absolute schemes (<c>https://…</c>) are rejected.
    /// </summary>
    private static bool IsSafeReturnPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2)
        {
            return false;
        }
        if (path[0] != '/')
        {
            return false;
        }
        // Reject "//foo" (protocol-relative) and "/\foo".
        if (path[1] == '/' || path[1] == '\\')
        {
            return false;
        }
        return true;
    }

    private static async Task<IResult> RevokeAsync(
        string sessionId,
        [FromServices] IGitHubOAuthService service,
        CancellationToken ct)
    {
        try
        {
            var revoked = await service.RevokeAsync(sessionId, ct);
            return revoked
                ? Results.NoContent()
                : Results.Problem(
                    detail: $"OAuth session '{sessionId}' is unknown.",
                    statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "GitHub OAuth is not configured",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> GetSessionAsync(
        string sessionId,
        [FromServices] IGitHubOAuthService service,
        CancellationToken ct)
    {
        var session = await service.GetSessionAsync(sessionId, ct);
        if (session is null)
        {
            return Results.Problem(
                detail: $"OAuth session '{sessionId}' is unknown.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new OAuthSessionResponse(
            SessionId: session.SessionId,
            Login: session.Login,
            UserId: session.UserId,
            Scopes: session.Scopes,
            ExpiresAt: session.ExpiresAt,
            CreatedAt: session.CreatedAt,
            ClientState: session.ClientState));
    }
}

/// <summary>
/// Request body for <c>POST /oauth/authorize</c>. Both fields are optional.
/// </summary>
/// <param name="Scopes">Per-request scope override; <c>null</c> falls back to the configured default.</param>
/// <param name="ClientState">Opaque state payload to echo back on the session after callback.</param>
public record OAuthAuthorizeRequest(IReadOnlyList<string>? Scopes, string? ClientState);

/// <summary>Response shape for <c>POST /oauth/authorize</c>.</summary>
/// <param name="AuthorizeUrl">The URL to redirect the user to.</param>
/// <param name="State">The state value stored server-side — surfaced for tests/debug, not secret.</param>
public record OAuthAuthorizeResponse(string AuthorizeUrl, string State);

/// <summary>Response shape for <c>GET /oauth/callback</c>.</summary>
/// <param name="SessionId">The issued session id. Caller uses this as the OAuth handle.</param>
/// <param name="Login">The GitHub login the session authenticates as.</param>
public record OAuthCallbackResponse(string SessionId, string Login);

/// <summary>Response shape for <c>GET /oauth/session/{sessionId}</c>.</summary>
public record OAuthSessionResponse(
    string SessionId,
    string Login,
    long UserId,
    string Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string? ClientState);