// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Commands;

using System.Text.Json;
using System.Web;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Inbound;
using Cvoya.Spring.Connectors;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps the Slack slash-command endpoints
/// (<c>POST /api/v1/tenant/connectors/slack/commands</c> and
/// <c>POST /api/v1/tenant/connectors/slack/interactions</c>) onto the
/// connector's route group. ADR-0061 §5.
///
/// <para>
/// Both endpoints share the signature-verification preamble used by
/// <see cref="SlackEventEndpoints"/>. Slash commands deliver form-
/// encoded bodies (Content-Type application/x-www-form-urlencoded);
/// the signature is computed over the raw body, so the endpoint
/// reads the raw bytes before parsing the form.
/// </para>
/// </summary>
public static class SlackCommandEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers <c>commands</c> + <c>interactions</c> on the
    /// supplied route group.
    /// </summary>
    public static void MapSlackCommandEndpoints(this IEndpointRouteBuilder group)
    {
        // ExcludeFromDescription: Slack delivers form-encoded bodies
        // that Kiota cannot model. The endpoints are operational
        // surfaces consumed by Slack itself, not by the platform's
        // typed clients — excluding them from the OpenAPI document
        // suppresses the Kiota "request body not an object type"
        // warnings without distorting the API generator output.
        group.MapPost("/commands", HandleCommandAsync)
            .WithName("HandleSlackCommand")
            .WithSummary("Receive a Slack slash-command invocation (ADR-0061 §5)")
            .WithTags("Connectors.Slack")
            .AllowAnonymous()
            .ExcludeFromDescription();

        group.MapPost("/interactions", HandleInteractionAsync)
            .WithName("HandleSlackInteraction")
            .WithSummary("Receive a Slack Block Kit interaction payload (modal submit etc.)")
            .WithTags("Connectors.Slack")
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleCommandAsync(
        HttpContext http,
        [FromServices] ITenantConnectorBindingStore bindingStore,
        [FromServices] ISlackSignatureValidator signatureValidator,
        [FromServices] ISlackCommandDispatcher commandDispatcher,
        [FromServices] Core.Secrets.ISecretResolver secretResolver,
        [FromServices] Core.Tenancy.ITenantContext tenantContext,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Connector.Slack.Commands.SlackCommandEndpoints");

        var rawBody = await SlackEventEndpoints.ReadRawBodyAsync(http, cancellationToken).ConfigureAwait(false);
        var timestamp = http.Request.Headers[SlackEventEndpoints.TimestampHeader].ToString();
        var signature = http.Request.Headers[SlackEventEndpoints.SignatureHeader].ToString();

        var form = ParseForm(rawBody);
        var teamId = form.TryGetValue("team_id", out var t) ? t : string.Empty;

        var (config, signingSecret) = await LoadSigningSecretAsync(
            bindingStore, secretResolver, tenantContext, teamId, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(signingSecret))
        {
            logger.LogInformation("Slack inbound: no binding / signing secret for team_id={TeamId}; rejecting.", teamId);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (!signatureValidator.Validate(rawBody, timestamp, signature, signingSecret))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Non-DM refusal: respond with an ephemeral message body
        // directly (the dispatcher records the audit + logs but the
        // user-facing copy is what slash commands accept as a 200
        // body).
        var channelName = form.TryGetValue("channel_name", out var cn) ? cn : string.Empty;
        if (!string.Equals(channelName, "directmessage", StringComparison.Ordinal))
        {
            await commandDispatcher.DispatchAsync(form, cancellationToken).ConfigureAwait(false);
            return Results.Json(new
            {
                response_type = "ephemeral",
                text = SlackCommandDispatcher.DmOnlyRefusalText,
            });
        }

        var outcome = await commandDispatcher.DispatchAsync(form, cancellationToken).ConfigureAwait(false);
        if (outcome == SlackCommandDispatchOutcome.UnknownCommand)
        {
            return Results.Json(new
            {
                response_type = "ephemeral",
                text = "Unknown slash command.",
            });
        }
        // Slack expects a 200 OK; ack with an empty body so the
        // modal that the dispatcher opened via views.open is what
        // Slack surfaces.
        return Results.Ok();
    }

    private static async Task<IResult> HandleInteractionAsync(
        HttpContext http,
        [FromServices] ITenantConnectorBindingStore bindingStore,
        [FromServices] ISlackSignatureValidator signatureValidator,
        [FromServices] ISlackCommandDispatcher commandDispatcher,
        [FromServices] Core.Secrets.ISecretResolver secretResolver,
        [FromServices] Core.Tenancy.ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var rawBody = await SlackEventEndpoints.ReadRawBodyAsync(http, cancellationToken).ConfigureAwait(false);
        var timestamp = http.Request.Headers[SlackEventEndpoints.TimestampHeader].ToString();
        var signature = http.Request.Headers[SlackEventEndpoints.SignatureHeader].ToString();

        // Slack interactions post the payload as form field
        // "payload" with a URL-encoded JSON document.
        var form = ParseForm(rawBody);
        if (!form.TryGetValue("payload", out var payloadJson) || string.IsNullOrEmpty(payloadJson))
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        var teamId = payload.TryGetProperty("team", out var team)
            && team.ValueKind == JsonValueKind.Object
            && team.TryGetProperty("id", out var teamIdProp)
            && teamIdProp.ValueKind == JsonValueKind.String
            ? teamIdProp.GetString() ?? string.Empty
            : string.Empty;

        var (_, signingSecret) = await LoadSigningSecretAsync(
            bindingStore, secretResolver, tenantContext, teamId, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(signingSecret))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (!signatureValidator.Validate(rawBody, timestamp, signature, signingSecret))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        await commandDispatcher.DispatchInteractionAsync(payload, cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    internal static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body))
        {
            return result;
        }
        foreach (var pair in body.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = HttpUtility.UrlDecode(pair[..eq]);
            var value = HttpUtility.UrlDecode(pair[(eq + 1)..]);
            if (!string.IsNullOrEmpty(key))
            {
                result[key] = value ?? string.Empty;
            }
        }
        return result;
    }

    private static async Task<(TenantSlackConfig? Config, string? SigningSecret)> LoadSigningSecretAsync(
        ITenantConnectorBindingStore bindingStore,
        Core.Secrets.ISecretResolver secretResolver,
        Core.Tenancy.ITenantContext tenantContext,
        string teamId,
        CancellationToken cancellationToken)
    {
        TenantConnectorBinding? binding;
        if (string.IsNullOrEmpty(teamId))
        {
            binding = await bindingStore
                .GetAsync(SlackInstallStore.ConnectorSlug, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            binding = await bindingStore
                .GetByExternalIdentityAsync(SlackInstallStore.ConnectorSlug, teamId, cancellationToken)
                .ConfigureAwait(false);
        }
        if (binding is null)
        {
            return (null, null);
        }
        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions);
        if (config is null)
        {
            return (null, null);
        }
        var resolution = await secretResolver
            .ResolveWithPathAsync(
                new Core.Secrets.SecretRef(
                    Core.Secrets.SecretScope.Tenant,
                    tenantContext.CurrentTenantId,
                    config.SigningSecretSecretName),
                cancellationToken)
            .ConfigureAwait(false);
        return (config, resolution.Value);
    }
}
