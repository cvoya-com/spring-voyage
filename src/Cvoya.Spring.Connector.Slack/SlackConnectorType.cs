// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connectors;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Slack concrete implementation of <see cref="IConnectorType"/>. Per
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md">ADR-0061</see>
/// §1 the Slack binding is <strong>tenant-scoped</strong>: one Slack
/// workspace per SV tenant, one bot identity per binding. There is no
/// per-unit Slack config.
///
/// <para>
/// OSS v0.1 restrictions (ADR-0061 §2) — single bound Slack user (the
/// OAuth installer), DM-only operation, Enterprise Grid refused at
/// install time, one workspace per OSS tenant. Forward-compat seams
/// for multi-user / multi-tenant generalisations are preserved per
/// ADR-0061 §7.
/// </para>
///
/// <para>
/// Scope of <em>this</em> file: only the connector identity surface
/// (slug, type id, binding scope, scaffolding endpoints, no-op
/// lifecycle hooks). OAuth install / disconnect lifecycle is handled
/// in <see cref="SlackOAuthEndpoints"/>; inbound event handling
/// (signature verification, auto-leave) is deferred to issue #2817;
/// outbound delivery is issue #2818; slash commands #2819.
/// </para>
/// </summary>
public class SlackConnectorType : IConnectorType
{
    /// <summary>
    /// Stable identity persisted on every Slack binding. Changing this
    /// value invalidates existing bindings — never change it in place.
    /// </summary>
    public static readonly Guid SlackTypeId =
        new("2c8d5b1f-9a4e-4f8b-b7c3-3e1d4a5b6c70");

    private readonly ILogger<SlackConnectorType> _logger;

    /// <summary>Creates a new <see cref="SlackConnectorType"/>.</summary>
    public SlackConnectorType(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SlackConnectorType>();
    }

    /// <inheritdoc />
    public Guid TypeId => SlackTypeId;

    /// <inheritdoc />
    public string Slug => "slack";

    /// <inheritdoc />
    public string DisplayName => "Slack";

    /// <inheritdoc />
    public string Description =>
        "Connect one Slack workspace to this SV tenant. One bound user (the OAuth installer), DM-only operation in OSS v0.1.";

    /// <inheritdoc />
    /// <remarks>
    /// ADR-0061 §1: the Slack binding lives on the tenant, not on a
    /// unit. The platform routes the connector's binding endpoints to
    /// the singular <c>/api/v1/tenant/connectors/slack/binding</c>
    /// surface and persists rows in
    /// <c>tenant_connector_bindings</c>.
    /// </remarks>
    public BindingScope BindingScope => BindingScope.Tenant;

    /// <inheritdoc />
    /// <remarks>
    /// Slack has no per-unit config — every binding-shaped data lives
    /// on the tenant-binding row. The platform's
    /// <see cref="IConnectorType.ConfigType"/> contract is still
    /// required, so this property surfaces the
    /// <see cref="TenantSlackConfig"/> shape which is the same row the
    /// tenant-binding endpoint round-trips. The generic
    /// <c>list-connectors</c> surface uses this for "what shape does
    /// the config take" reflection.
    /// </remarks>
    public Type ConfigType => typeof(TenantSlackConfig);

    /// <inheritdoc />
    /// <remarks>
    /// ADR-0047 §4: per-<c>TenantUser</c> display-identity row holding
    /// the user's Slack handle / display name. The OSS bound user is
    /// the operator; multi-user installs in cloud carry one row per
    /// bound Slack user.
    /// </remarks>
    public Type? UserConfigType => typeof(TenantSlackUserConfig);

    /// <inheritdoc />
    public void MapRoutes(IEndpointRouteBuilder group)
    {
        // Health-shaped scaffolding endpoint so registration is
        // observable (the brief calls this out explicitly). The
        // tenant-binding endpoints live on the generic platform
        // surface at /api/v1/tenant/connectors/slack/binding — they
        // are mapped by ConnectorEndpoints.MapConnectorEndpoints, not
        // here.
        group.MapGet("/healthz", () => Results.Ok(new
        {
            slug = "slack",
            registered = true,
            bindingScope = "Tenant",
        }))
            .WithName("SlackConnectorHealth")
            .WithSummary("Liveness probe — confirms the Slack connector is registered with the host")
            .WithTags("Connectors.Slack")
            .Produces<object>(StatusCodes.Status200OK);

        // OAuth install + disconnect endpoints (ADR-0061 §2.3, §2.5).
        group.MapSlackOAuthEndpoints();

        // Config-schema parity with the other connectors so portal /
        // CLI clients can introspect the shape they should send when
        // PUT-ing the binding.
        group.MapGet("/config-schema", () => Results.Ok(BuildConfigSchema()))
            .WithName("GetSlackConnectorConfigSchema")
            .WithSummary("Get the JSON Schema describing the Slack tenant-binding config body")
            .WithTags("Connectors.Slack")
            .Produces<JsonElement>(StatusCodes.Status200OK);

        // TODO(#2819): slash-command endpoints (/sv-thread, /sv-threads, /sv-help).
        // TODO(#2817): events endpoint with signature verification.
    }

    /// <inheritdoc />
    public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<JsonElement?>(BuildConfigSchema());

    /// <inheritdoc />
    public Task<JsonElement?> GetUserConfigSchemaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<JsonElement?>(BuildUserConfigSchema());

    /// <inheritdoc />
    /// <remarks>
    /// ADR-0061 §1: Slack binds at the tenant scope, not the unit
    /// scope. The per-unit lifecycle hooks therefore do nothing — no
    /// per-unit Slack resources to register or tear down at unit
    /// start / stop.
    /// </remarks>
    public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Slack connector unit start is a no-op (tenant-scoped binding); unit={UnitId}", unitId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Slack connector unit stop is a no-op (tenant-scoped binding); unit={UnitId}", unitId);
        return Task.CompletedTask;
    }

    internal static JsonElement BuildConfigSchema()
    {
        const string schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "TenantSlackConfig",
          "type": "object",
          "description": "Per-tenant Slack binding config (ADR-0061 §1). Populated by the OAuth callback; manual PUTs target this shape.",
          "properties": {
            "team_id": {
              "type": "string",
              "description": "The Slack workspace id (team.id from the OAuth response)."
            },
            "team_name": {
              "type": ["string", "null"],
              "description": "Display name of the Slack workspace."
            },
            "bot_user_id": {
              "type": "string",
              "description": "Slack user_id of the bot identity created by the OAuth install."
            },
            "bot_token_secret_name": {
              "type": "string",
              "description": "Tenant-secret name addressing the bot OAuth access token (ADR-0003)."
            },
            "signing_secret_secret_name": {
              "type": "string",
              "description": "Tenant-secret name addressing the Slack app's signing secret."
            },
            "installer_user_id": {
              "type": "string",
              "description": "Slack user_id of the OAuth installer (the bound user in OSS v0.1)."
            },
            "single_user_mode": {
              "type": "boolean",
              "description": "ADR-0061 §7.3: gate for the auto-leave / scope-omission behaviour. Defaults true in OSS."
            },
            "mode": {
              "type": "string",
              "enum": ["Workspace", "Org"],
              "description": "ADR-0061 §7.6: SlackBindingMode. v0.1 only persists Workspace; Org is the forward-compat slot."
            },
            "bound_users": {
              "type": "array",
              "description": "ADR-0061 §7.1: list of (slack_user_id, tenant_user_id) mappings. Length 1 in OSS; length N in cloud.",
              "items": {
                "type": "object",
                "properties": {
                  "slack_user_id": { "type": "string" },
                  "tenant_user_id": { "type": "string", "format": "uuid" }
                },
                "required": ["slack_user_id", "tenant_user_id"]
              }
            }
          },
          "required": ["team_id", "bot_user_id", "bot_token_secret_name", "signing_secret_secret_name", "installer_user_id", "single_user_mode", "mode", "bound_users"]
        }
        """;
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }

    internal static JsonElement BuildUserConfigSchema()
    {
        const string schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "TenantSlackUserConfig",
          "type": "object",
          "description": "Per-TenantUser Slack display identity (ADR-0047 §4). Strictly display-only; no auth fields.",
          "properties": {
            "slack_user_id": {
              "type": "string",
              "description": "The Slack user_id (e.g. 'U123456'); never used as an authorization principal — display only."
            },
            "display_name": {
              "type": ["string", "null"],
              "description": "Optional human-friendly handle for this user (e.g. 'alex' or 'Alex Smith')."
            }
          },
          "required": ["slack_user_id"]
        }
        """;
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }
}
