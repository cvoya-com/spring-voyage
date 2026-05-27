// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Maps the Slack OAuth + binding-lifecycle endpoints onto the route
/// group passed in by <see cref="SlackConnectorType.MapRoutes"/>. The
/// routes live under
/// <c>/api/v1/tenant/connectors/slack/oauth/…</c> and
/// <c>/api/v1/tenant/connectors/slack/disconnect</c> because the
/// platform pre-scopes the outer prefix for every connector.
///
/// <para>
/// Per ADR-0061 §2.3 / §2.5 / §7.5 the flow is:
/// </para>
/// <list type="number">
///   <item><description><b>authorize</b> — build the Slack consent URL and persist the OAuth state.</description></item>
///   <item><description><b>callback</b> — exchange <c>code</c> for an access token via <c>oauth.v2.access</c>, call <c>team.info</c> to detect Enterprise Grid, then persist the binding + workspace-map row + tenant secrets atomically. Renders an HTML page that posts the outcome back to <c>window.opener</c> per issue #2837.</description></item>
///   <item><description><b>disconnect</b> — call <c>auth.revoke</c> against the bot token, then delete the binding row + workspace-map row + tenant secrets.</description></item>
/// </list>
/// </summary>
public static class SlackOAuthEndpoints
{
    /// <summary>
    /// <c>type</c> field on the OAuth-callback handoff message. The
    /// portal's <c>slack-oauth-browser.ts</c> listens for messages
    /// matching this value and ignores anything else.
    /// </summary>
    public const string CallbackMessageType = "sv:slack:oauth:done";

    /// <summary>
    /// Registers <c>oauth/authorize</c>, <c>oauth/callback</c>, and
    /// <c>disconnect</c> endpoints on the supplied builder.
    /// </summary>
    public static void MapSlackOAuthEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/oauth/authorize", AuthorizeAsync)
            .WithName("BeginSlackOAuthAuthorization")
            .WithSummary("Start a Slack OAuth install flow and return the consent URL")
            .WithTags("Connectors.Slack.OAuth")
            .Accepts<SlackAuthorizeRequest>("application/json")
            .Produces<SlackAuthorizeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        // Issue #2837: the callback now returns an HTML page that fires
        // a `postMessage` to the popup's opener, mirroring the GitHub
        // connector flow. The HTTP status codes are unchanged so any
        // direct-API caller (CLI / curl) still sees the same outcome
        // discrimination.
        group.MapGet("/oauth/callback", CallbackAsync)
            .WithName("HandleSlackOAuthCallback")
            .WithSummary("Consume the Slack OAuth callback and notify the portal opener")
            .WithTags("Connectors.Slack.OAuth")
            .Produces<string>(StatusCodes.Status200OK, "text/html")
            .Produces<string>(StatusCodes.Status400BadRequest, "text/html")
            .Produces<string>(StatusCodes.Status409Conflict, "text/html")
            .Produces<string>(StatusCodes.Status422UnprocessableEntity, "text/html")
            .Produces<string>(StatusCodes.Status502BadGateway, "text/html");

        group.MapPost("/disconnect", DisconnectAsync)
            .WithName("DisconnectSlackBinding")
            .WithSummary("Revoke the Slack OAuth token and delete the tenant binding")
            .WithTags("Connectors.Slack")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> AuthorizeAsync(
        [FromBody] SlackAuthorizeRequest? request,
        [FromServices] ISlackOAuthService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.BeginAuthorizationAsync(
                clientState: request?.ClientState,
                cancellationToken);
            return Results.Ok(new SlackAuthorizeResponse(result.AuthorizeUrl, result.State));
        }
        catch (InvalidOperationException ex)
        {
            // Slack OAuth options not configured — surface as 502 so the
            // wizard can render the "operator must configure Slack OAuth"
            // panel instead of pretending the call would have succeeded.
            return Results.Problem(
                title: "Slack OAuth is not configured",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> CallbackAsync(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromServices] ISlackOAuthStateStore stateStore,
        [FromServices] ISlackOAuthService service,
        CancellationToken cancellationToken)
    {
        // User-cancelled consent (Slack appends ?error=access_denied or
        // similar). The state token has not been consumed yet, so we
        // peek-and-consume directly to surface the portal's targetOrigin
        // on the HTML handoff page — and to invalidate the token so it
        // can't be reused. This mirrors the GitHub connector's
        // ConsumeTargetOriginAsync path.
        if (!string.IsNullOrEmpty(error))
        {
            var clientStateForError = await ConsumeClientStateAsync(state, stateStore, cancellationToken);
            return CallbackPage(
                status: CallbackStatus.Error,
                errorCode: SlugifyError(error),
                message: $"Slack returned error '{error}' on the OAuth callback.",
                clientState: clientStateForError,
                httpStatus: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return CallbackPage(
                status: CallbackStatus.Error,
                errorCode: "invalid_request",
                message: "Both 'code' and 'state' query parameters are required.",
                clientState: null,
                httpStatus: StatusCodes.Status400BadRequest);
        }

        SlackCallbackOutcome outcome;
        try
        {
            outcome = await service.HandleCallbackAsync(code, state, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Slack OAuth options not configured — exposed via the same
            // HTML handoff shape so the portal can show the operator
            // hint without parsing JSON.
            return CallbackPage(
                status: CallbackStatus.Error,
                errorCode: "oauth_not_configured",
                message: ex.Message,
                clientState: null,
                httpStatus: StatusCodes.Status502BadGateway);
        }

        return outcome switch
        {
            SlackCallbackOutcome.Success success => CallbackPage(
                status: CallbackStatus.Success,
                errorCode: null,
                message: $"Slack workspace '{success.TeamId}' connected.",
                clientState: success.ClientState,
                httpStatus: StatusCodes.Status200OK),

            SlackCallbackOutcome.EnterpriseGridUnsupported grid => CallbackPage(
                status: CallbackStatus.Error,
                errorCode: "SlackEnterpriseGridUnsupported",
                message: grid.Reason,
                clientState: grid.ClientState,
                httpStatus: StatusCodes.Status422UnprocessableEntity),

            SlackCallbackOutcome.WorkspaceConflict conflict => CallbackPage(
                status: CallbackStatus.Error,
                errorCode: "SlackWorkspaceConflict",
                message: conflict.Reason,
                clientState: conflict.ClientState,
                httpStatus: StatusCodes.Status409Conflict),

            SlackCallbackOutcome.InvalidState => CallbackPage(
                status: CallbackStatus.Error,
                errorCode: "invalid_state",
                message: "The OAuth state parameter is unknown or has expired.",
                clientState: null,
                httpStatus: StatusCodes.Status400BadRequest),

            SlackCallbackOutcome.ExchangeFailed ex => CallbackPage(
                status: CallbackStatus.Error,
                errorCode: "exchange_failed",
                message: ex.Reason,
                clientState: ex.ClientState,
                httpStatus: StatusCodes.Status502BadGateway),

            _ => CallbackPage(
                status: CallbackStatus.Error,
                errorCode: "unknown_outcome",
                message: "Unknown OAuth callback outcome.",
                clientState: null,
                httpStatus: StatusCodes.Status502BadGateway),
        };
    }

    private static async Task<IResult> DisconnectAsync(
        [FromServices] ISlackOAuthService service,
        CancellationToken cancellationToken)
    {
        var outcome = await service.DisconnectAsync(cancellationToken);
        return outcome switch
        {
            SlackDisconnectOutcome.Removed => Results.NoContent(),
            SlackDisconnectOutcome.NotBound => Results.Problem(
                detail: "No Slack binding exists for this tenant.",
                statusCode: StatusCodes.Status404NotFound),
            SlackDisconnectOutcome.RevokeFailed ex => Results.Problem(
                title: "Slack token revoke failed",
                detail: ex.Reason,
                statusCode: StatusCodes.Status502BadGateway),
            _ => Results.Problem(
                detail: "Unknown disconnect outcome.",
                statusCode: StatusCodes.Status502BadGateway),
        };
    }

    /// <summary>
    /// Renders the HTML handoff page that fires a single
    /// <c>postMessage</c> to <c>window.opener</c> and then closes
    /// itself. When no concrete <c>targetOrigin</c> can be derived
    /// from <paramref name="clientState"/>, the page only shows the
    /// fallback message and does NOT call <c>postMessage</c> — per
    /// issue #2837's security note, we never broadcast to <c>*</c>.
    /// The binding is already persisted at this point, so the portal
    /// will pick up the bound state on the next mount.
    /// </summary>
    private static IResult CallbackPage(
        CallbackStatus status,
        string? errorCode,
        string message,
        string? clientState,
        int httpStatus)
    {
        var statusText = status == CallbackStatus.Success ? "success" : "error";
        var payload = status == CallbackStatus.Success
            ? (object)new { type = CallbackMessageType, status = statusText }
            : new
            {
                type = CallbackMessageType,
                status = statusText,
                error = errorCode,
                message,
            };

        var messageJson = JsonSerializer.Serialize(payload);
        var targetOrigin = TryReadTargetOrigin(clientState);
        var title = status == CallbackStatus.Success
            ? "Slack workspace connected"
            : "Slack install failed";
        var fallback = status == CallbackStatus.Success
            ? "You can close this tab and return to Spring Voyage."
            : "You can close this tab and return to Spring Voyage to try again.";

        // postMessage payload is the same shape whether or not we have
        // a targetOrigin. The script only runs the postMessage call
        // when both the opener exists AND we have a concrete target —
        // otherwise it falls back to closing the window without
        // notifying anyone. The portal's existing popup-closed timeout
        // is the safety net.
        var postMessageScript = targetOrigin is null
            ? "/* postMessage skipped: no targetOrigin in clientState */"
            : $"if (window.opener && !window.opener.closed) {{ window.opener.postMessage({messageJson}, {JsonSerializer.Serialize(targetOrigin)}); }}";

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
                <p>{{fallback}}</p>
              </main>
              <script>
                (() => {
                  try {
                    {{postMessageScript}}
                  } catch {
                    /* postMessage may throw if the opener navigated away */
                  }
                  setTimeout(() => {
                    try { window.close(); } catch { /* close may be blocked by the UA */ }
                  }, 100);
                })();
              </script>
            </body>
            </html>
            """;

        return Results.Content(html, "text/html; charset=utf-8", statusCode: httpStatus);
    }

    /// <summary>
    /// Reads <c>targetOrigin</c> out of the portal-supplied
    /// <paramref name="clientState"/> JSON blob and validates it is a
    /// concrete http(s) origin. Returns <c>null</c> when the input is
    /// missing, malformed, or anything other than a valid http(s)
    /// authority.
    /// </summary>
    internal static string? TryReadTargetOrigin(string? clientState)
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

    private static async Task<string?> ConsumeClientStateAsync(
        string? state,
        ISlackOAuthStateStore stateStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var entry = await stateStore.ConsumeAsync(state, cancellationToken);
        return entry?.ClientState;
    }

    /// <summary>
    /// Slack OAuth error codes are vendor-defined (e.g. <c>access_denied</c>);
    /// pass them through verbatim but clip noisy whitespace to keep
    /// the machine-readable field stable for the portal's switch.
    /// </summary>
    private static string SlugifyError(string error) => error.Trim();

    private enum CallbackStatus
    {
        Success,
        Error,
    }
}

/// <summary>Request body for <c>POST /oauth/authorize</c>.</summary>
/// <param name="ClientState">
/// Opaque state echoed back via the callback for cross-tab handoff
/// (e.g. the portal's <c>targetOrigin</c>). Optional.
/// </param>
public record SlackAuthorizeRequest(string? ClientState);

/// <summary>Response body for <c>POST /oauth/authorize</c>.</summary>
/// <param name="AuthorizeUrl">URL the operator's browser must visit to grant the install.</param>
/// <param name="State">The opaque OAuth state token (surfaced for debugging; never secret).</param>
public record SlackAuthorizeResponse(string AuthorizeUrl, string State);

