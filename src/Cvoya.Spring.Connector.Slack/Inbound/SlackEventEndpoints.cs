// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

using System.Text;
using System.Text.Json;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connectors;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps the Slack events endpoint
/// (<c>POST /api/v1/tenant/connectors/slack/events</c>) onto the
/// connector's route group. ADR-0061 §2.2 / §2.4 / §3.
///
/// <para>
/// Signature verification preamble per Slack's verifying-requests-from-
/// slack rule (HMAC-SHA256 over <c>v0:&lt;timestamp&gt;:&lt;rawBody&gt;</c>
/// keyed on the binding's <c>signing_secret</c>). Both stale-timestamp
/// and mismatching-signature responses are 401 with no body.
/// </para>
/// <para>
/// <c>url_verification</c> challenges short-circuit before dispatch
/// and return the <c>challenge</c> string verbatim with status 200.
/// </para>
/// <para>
/// All other event types are handed to <see cref="ISlackEventDispatcher"/>.
/// </para>
/// </summary>
public static class SlackEventEndpoints
{
    /// <summary>Slack-supplied timestamp header used in the signature base string.</summary>
    public const string TimestampHeader = "X-Slack-Request-Timestamp";

    /// <summary>Slack-supplied signature header.</summary>
    public const string SignatureHeader = "X-Slack-Signature";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers <c>events</c> on the supplied route builder. The
    /// platform's connector-mount path applies
    /// <c>RequireAuthorization(TenantUser)</c> by default; Slack
    /// events arrive unauthenticated (signed instead), so the
    /// endpoint marks itself <see cref="AllowAnonymousAttribute"/>.
    /// </summary>
    public static void MapSlackEventEndpoints(this IEndpointRouteBuilder group)
    {
        // ExcludeFromDescription: the events endpoint is consumed by
        // Slack itself, not by the platform's typed clients. Slack's
        // payload shape is unmodeled JSON whose schema lives on
        // Slack's side; surfacing it in our OpenAPI document only
        // adds noise the typed clients would need to ignore.
        group.MapPost("/events", HandleAsync)
            .WithName("HandleSlackEvent")
            .WithSummary("Receive a Slack Events API delivery (ADR-0061 §2.2 / §3)")
            .WithTags("Connectors.Slack")
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        [FromServices] ITenantConnectorBindingStore bindingStore,
        [FromServices] ISlackSignatureValidator signatureValidator,
        [FromServices] ISlackEventDispatcher eventDispatcher,
        [FromServices] Core.Secrets.ISecretResolver secretResolver,
        [FromServices] Core.Tenancy.ITenantContext tenantContext,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Connector.Slack.Inbound.SlackEventEndpoints");

        var rawBody = await ReadRawBodyAsync(http, cancellationToken).ConfigureAwait(false);
        var timestamp = http.Request.Headers[TimestampHeader].ToString();
        var signature = http.Request.Headers[SignatureHeader].ToString();

        // url_verification challenges must round-trip the challenge
        // value before any signature work. Slack signs the challenge
        // requests too, so we still verify the signature before
        // echoing — that is the contract Slack documents.
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Slack inbound: failed to parse JSON body.");
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        // Resolve the binding by team_id (carries the signing secret).
        var teamId = payload.TryGetProperty("team_id", out var teamIdProp) && teamIdProp.ValueKind == JsonValueKind.String
            ? teamIdProp.GetString()
            : null;

        if (string.IsNullOrEmpty(teamId))
        {
            // url_verification carries the challenge but no team_id —
            // Slack issues a single one of these per app, and
            // verifying the signature requires the secret. There is
            // exactly one configured Slack binding in OSS (multi-
            // tenant cloud handles this differently), so we look it
            // up via GetAsync in the current tenant context. The
            // endpoint is anonymous; tenant context will resolve to
            // the OSS default when no auth context is present.
            var bindingNoTeam = await bindingStore
                .GetAsync(SlackInstallStore.ConnectorSlug, cancellationToken)
                .ConfigureAwait(false);
            if (bindingNoTeam is null)
            {
                logger.LogWarning("Slack inbound: no Slack binding configured; rejecting unsigned request.");
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var configNoTeam = bindingNoTeam.Config.Deserialize<TenantSlackConfig>(JsonOptions);
            if (configNoTeam is null)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var signingSecretNoTeam = await ReadSigningSecretAsync(secretResolver, tenantContext, configNoTeam, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(signingSecretNoTeam))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (!signatureValidator.Validate(rawBody, timestamp, signature, signingSecretNoTeam))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            return HandleUrlVerification(payload);
        }

        var binding = await bindingStore
            .GetByExternalIdentityAsync(SlackInstallStore.ConnectorSlug, teamId, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null)
        {
            logger.LogInformation("Slack inbound: no tenant binding for team_id={TeamId}; rejecting.", teamId);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions);
        if (config is null)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var signingSecret = await ReadSigningSecretAsync(secretResolver, tenantContext, config, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(signingSecret))
        {
            logger.LogWarning("Slack inbound: signing secret not resolvable for team_id={TeamId}.", teamId);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (!signatureValidator.Validate(rawBody, timestamp, signature, signingSecret))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // url_verification short-circuit (after signature is valid).
        if (IsUrlVerification(payload))
        {
            return HandleUrlVerification(payload);
        }

        await eventDispatcher.DispatchAsync(payload, cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    private static bool IsUrlVerification(JsonElement payload)
    {
        return payload.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String
            && string.Equals(typeProp.GetString(), "url_verification", StringComparison.Ordinal);
    }

    private static IResult HandleUrlVerification(JsonElement payload)
    {
        var challenge = payload.TryGetProperty("challenge", out var challengeProp)
            && challengeProp.ValueKind == JsonValueKind.String
            ? challengeProp.GetString()
            : null;
        if (string.IsNullOrEmpty(challenge))
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }
        return Results.Text(challenge, "text/plain");
    }

    internal static async Task<string> ReadRawBodyAsync(HttpContext http, CancellationToken cancellationToken)
    {
        http.Request.EnableBuffering();
        using var reader = new StreamReader(
            http.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        http.Request.Body.Position = 0;
        return body;
    }

    private static async Task<string?> ReadSigningSecretAsync(
        Core.Secrets.ISecretResolver resolver,
        Core.Tenancy.ITenantContext tenantContext,
        TenantSlackConfig config,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver
            .ResolveWithPathAsync(
                new Core.Secrets.SecretRef(
                    Core.Secrets.SecretScope.Tenant,
                    tenantContext.CurrentTenantId,
                    config.SigningSecretSecretName),
                cancellationToken)
            .ConfigureAwait(false);
        return resolution.Value;
    }
}
