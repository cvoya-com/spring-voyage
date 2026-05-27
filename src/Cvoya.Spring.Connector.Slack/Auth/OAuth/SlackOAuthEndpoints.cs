// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

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
///   <item><description><b>callback</b> — exchange <c>code</c> for an access token via <c>oauth.v2.access</c>, call <c>team.info</c> to detect Enterprise Grid, then persist the binding + workspace-map row + tenant secrets atomically.</description></item>
///   <item><description><b>disconnect</b> — call <c>auth.revoke</c> against the bot token, then delete the binding row + workspace-map row + tenant secrets.</description></item>
/// </list>
/// </summary>
public static class SlackOAuthEndpoints
{
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

        group.MapGet("/oauth/callback", CallbackAsync)
            .WithName("HandleSlackOAuthCallback")
            .WithSummary("Consume the Slack OAuth callback and persist the tenant binding")
            .WithTags("Connectors.Slack.OAuth")
            .Produces<SlackCallbackResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);

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
        [FromServices] ISlackOAuthService service,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return Results.Problem(
                title: "Slack OAuth was declined",
                detail: $"Slack returned error '{error}' on the OAuth callback.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return Results.Problem(
                detail: "Both 'code' and 'state' query parameters are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await service.HandleCallbackAsync(code, state, cancellationToken);
        return outcome switch
        {
            SlackCallbackOutcome.Success success => Results.Ok(new SlackCallbackResponse(
                TeamId: success.TeamId,
                BotUserId: success.BotUserId,
                InstallerUserId: success.InstallerUserId)),

            SlackCallbackOutcome.EnterpriseGridUnsupported grid => Results.Problem(
                title: "Slack Enterprise Grid is not supported",
                detail: grid.Reason,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "SlackEnterpriseGridUnsupported",
                    ["enterprise_id"] = grid.EnterpriseId,
                }),

            SlackCallbackOutcome.WorkspaceConflict conflict => Results.Problem(
                title: "Workspace already bound",
                detail: conflict.Reason,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "SlackWorkspaceConflict",
                    ["expected_team_id"] = conflict.ExpectedTeamId,
                    ["received_team_id"] = conflict.ReceivedTeamId,
                }),

            SlackCallbackOutcome.InvalidState => Results.Problem(
                detail: "The OAuth state parameter is unknown or has expired.",
                statusCode: StatusCodes.Status400BadRequest),

            SlackCallbackOutcome.ExchangeFailed ex => Results.Problem(
                title: "Slack token exchange failed",
                detail: ex.Reason,
                statusCode: StatusCodes.Status502BadGateway),

            _ => Results.Problem(
                detail: "Unknown OAuth callback outcome.",
                statusCode: StatusCodes.Status502BadGateway),
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

/// <summary>
/// Success response body for <c>GET /oauth/callback</c>. ADR-0061
/// §7.5: the callback resolves <c>team_id → tenant_id</c> via the
/// workspace-map and persists the binding row + the secrets.
/// </summary>
/// <param name="TeamId">Slack workspace id recorded on the binding.</param>
/// <param name="BotUserId">Slack <c>user_id</c> of the bot identity issued by the install.</param>
/// <param name="InstallerUserId">Slack <c>user_id</c> of the OAuth installer (the bound user).</param>
public record SlackCallbackResponse(string TeamId, string BotUserId, string InstallerUserId);
