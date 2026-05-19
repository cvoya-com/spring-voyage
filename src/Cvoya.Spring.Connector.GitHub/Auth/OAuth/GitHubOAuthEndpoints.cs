// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using System.Text.Json;

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
            .WithSummary("Consume the OAuth callback, issue a session, and notify the portal opener")
            .WithTags("Connectors.GitHub.OAuth")
            .Produces<string>(StatusCodes.Status200OK, "text/html")
            .Produces<string>(StatusCodes.Status400BadRequest, "text/html")
            .Produces<string>(StatusCodes.Status502BadGateway, "text/html");

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
        [FromBody] OAuthAuthorizeRequest request,
        [FromServices] IGitHubOAuthService service,
        CancellationToken ct)
    {
        try
        {
            // ADR-0047 §13: the authorize body grows two optional fields
            // that carry the intent payload through the state store and
            // into the callback. Both default to the legacy unspecified
            // flow when omitted, so pre-ADR-0047 callers (e.g. the existing
            // portal "Link GitHub" panel that powers list-repositories)
            // continue to work unchanged. The body itself is required —
            // pre-ADR-0047 callers post an empty JSON object `{}`.
            var initiation = TryBuildInitiation(request);
            var result = await service.BeginAuthorizationAsync(
                scopesOverride: request.Scopes,
                clientState: request.ClientState,
                initiation: initiation,
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

    private static OAuthInitiationContext? TryBuildInitiation(OAuthAuthorizeRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        var intent = request.Intent switch
        {
            null or "" => OAuthInitiationIntent.Unspecified,
            "user-identity" => OAuthInitiationIntent.UserIdentitySurface,
            "binding-wizard" => OAuthInitiationIntent.BindingWizard,
            _ => OAuthInitiationIntent.Unspecified,
        };

        if (intent == OAuthInitiationIntent.Unspecified
            && request.TenantUserId is null
            && request.BindingId is null)
        {
            return null;
        }

        return new OAuthInitiationContext(
            Intent: intent,
            TenantUserId: request.TenantUserId,
            BindingId: request.BindingId);
    }

    private static async Task<IResult> CallbackAsync(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        [FromServices] IOAuthStateStore stateStore,
        [FromServices] IGitHubOAuthService service,
        CancellationToken ct)
    {
        // GitHub forwards user-initiated failures (e.g. the user declined
        // consent) on the query string rather than a non-2xx. Surface those
        // unchanged so the portal can display GitHub's own wording.
        if (!string.IsNullOrEmpty(error))
        {
            var targetOrigin = await ConsumeTargetOriginAsync(state, stateStore, ct);
            return CallbackPage(
                sessionId: null,
                login: null,
                patSecretName: null,
                bindingId: null,
                error: error,
                reason: errorDescription ?? error,
                targetOrigin: targetOrigin,
                statusCode: StatusCodes.Status400BadRequest);
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
                return CallbackPage(
                    sessionId: null,
                    login: null,
                    patSecretName: null,
                    bindingId: null,
                    error: result.Error ?? "callback_failed",
                    reason: result.ErrorDescription ?? result.Error,
                    targetOrigin: null,
                    statusCode: status);
            }

            var session = await service.GetSessionAsync(result.SessionId, ct);
            return CallbackPage(
                sessionId: result.SessionId,
                login: result.Login,
                patSecretName: result.PatSecretName,
                bindingId: result.BindingId,
                error: null,
                reason: null,
                targetOrigin: TryReadTargetOrigin(session?.ClientState),
                statusCode: StatusCodes.Status200OK);
        }
        catch (InvalidOperationException ex)
        {
            return CallbackPage(
                sessionId: null,
                login: null,
                patSecretName: null,
                bindingId: null,
                error: "oauth_not_configured",
                reason: ex.Message,
                targetOrigin: null,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult CallbackPage(
        string? sessionId,
        string? login,
        string? patSecretName,
        Guid? bindingId,
        string? error,
        string? reason,
        string? targetOrigin,
        int statusCode)
    {
        // ADR-0047 §13: pass the persisted secret name + binding id through
        // the browser handoff so the wizard can wire `pat_secret_name`
        // without a second API call. Identity-surface callers ignore the
        // fields; wizard callers read them off both the postMessage payload
        // and the localStorage fallback for cross-tab handoff.
        var message = JsonSerializer.Serialize(new
        {
            type = "spring-voyage:github-oauth-session",
            sessionId,
            login,
            patSecretName,
            bindingId = bindingId?.ToString("N"),
            error,
            reason,
        });
        var storageKey = JsonSerializer.Serialize("springvoyage:github-oauth-callback");
        var target = targetOrigin is null
            ? "window.location.origin"
            : JsonSerializer.Serialize(targetOrigin);
        var title = error is null ? "GitHub account linked" : "GitHub authorization failed";
        var detail = error is null
            ? "You can return to Spring Voyage."
            : "Return to Spring Voyage and try linking your GitHub account again.";

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{title}}</title>
            </head>
            <body>
              <main>
                <h1>{{title}}</h1>
                <p>{{detail}}</p>
              </main>
              <script>
                (() => {
                  const message = {{message}};
                  const targetOrigin = {{target}};
                  try {
                    if (window.opener && !window.opener.closed) {
                      window.opener.postMessage(message, targetOrigin);
                    }
                  } catch {
                  }
                  try {
                    window.localStorage.setItem({{storageKey}}, JSON.stringify({
                      ...message,
                      deliveredAt: Date.now()
                    }));
                  } catch {
                  }
                  window.close();
                })();
              </script>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    private static string? TryReadTargetOrigin(string? clientState)
    {
        if (string.IsNullOrWhiteSpace(clientState))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(clientState);
            if (!document.RootElement.TryGetProperty("targetOrigin", out var originElement) ||
                originElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var origin = originElement.GetString();
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            return uri.GetLeftPart(UriPartial.Authority);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ConsumeTargetOriginAsync(
        string? state,
        IOAuthStateStore stateStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var entry = await stateStore.ConsumeAsync(state, ct);
        return TryReadTargetOrigin(entry?.ClientState);
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
            ClientState: session.ClientState,
            PatSecretName: session.PatSecretName,
            BindingId: session.Initiation?.BindingId?.ToString("N")));
    }
}

/// <summary>
/// Request body for <c>POST /oauth/authorize</c>. Fields are optional;
/// callers that omit the body entirely get the legacy
/// <see cref="OAuthInitiationIntent.Unspecified"/> flow that powers the
/// existing <c>list-repositories</c> wizard panel.
/// </summary>
/// <param name="Scopes">Per-request scope override; <c>null</c> falls back to the configured default.</param>
/// <param name="ClientState">Opaque state payload to echo back on the session after callback.</param>
/// <param name="Intent">
/// Declared intent per ADR-0047 §13. Accepted values:
/// <c>user-identity</c> (the user-identity surface — persist the token
/// and refresh the caller's GitHub display identity),
/// <c>binding-wizard</c> (the new-unit wizard — persist the token under
/// the wizard-supplied <paramref name="BindingId"/> so the binding-create
/// call can wire <c>pat_secret_name</c>), or omitted / unknown (legacy
/// session-only flow).
/// </param>
/// <param name="TenantUserId">
/// The calling tenant user's stable UUID. Required for the
/// <c>user-identity</c> intent so the callback knows whose display
/// identity to refresh.
/// </param>
/// <param name="BindingId">
/// Pre-minted binding UUID for the <c>binding-wizard</c> intent (ADR-0047
/// §13 option (a)). The callback persists the token under
/// <c>binding/&lt;BindingId-no-dash&gt;/github/pat</c> so the wizard's
/// subsequent binding-create call references the same id without
/// rewriting the secret.
/// </param>
public record OAuthAuthorizeRequest(
    IReadOnlyList<string>? Scopes,
    string? ClientState,
    string? Intent = null,
    Guid? TenantUserId = null,
    Guid? BindingId = null);

/// <summary>Response shape for <c>POST /oauth/authorize</c>.</summary>
/// <param name="AuthorizeUrl">The URL to redirect the user to.</param>
/// <param name="State">The state value stored server-side — surfaced for tests/debug, not secret.</param>
public record OAuthAuthorizeResponse(string AuthorizeUrl, string State);

/// <summary>Response shape for <c>GET /oauth/session/{sessionId}</c>.</summary>
/// <param name="SessionId">Server-issued opaque session id.</param>
/// <param name="Login">The GitHub login of the authorized user.</param>
/// <param name="UserId">The GitHub numeric user id.</param>
/// <param name="Scopes">Space-joined OAuth scopes GitHub actually granted.</param>
/// <param name="ExpiresAt">When the token expires, or <c>null</c> when no expiry was advertised.</param>
/// <param name="CreatedAt">When the session record was created.</param>
/// <param name="ClientState">Opaque state payload echoed back from the authorize request.</param>
/// <param name="PatSecretName">
/// Tenant-scoped secret name the OAuth-issued token was persisted under
/// per ADR-0047 §5, or <c>null</c> for the legacy flow that does not
/// persist a binding-usable PAT. CLI / portal callers read this through
/// the post-callback session lookup so the wizard can wire
/// <c>pat_secret_name</c> on the binding-create call.
/// </param>
/// <param name="BindingId">
/// Binding UUID the secret is addressed by (no-dash hex form). Matches
/// the value the wizard supplied on the authorize call for the
/// <c>binding-wizard</c> intent; <c>null</c> for the legacy flow.
/// </param>
public record OAuthSessionResponse(
    string SessionId,
    string Login,
    long UserId,
    string Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string? ClientState,
    string? PatSecretName = null,
    string? BindingId = null);
