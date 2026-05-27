// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack;

using System.Text.Json.Serialization;

/// <summary>
/// Per-tenant Slack binding config (ADR-0061 §1). The shape persisted
/// in <c>tenant_connector_bindings.config</c> for the Slack connector.
/// The platform never deserialises this — only the Slack package does.
///
/// <para>
/// Populated by the OAuth callback (ADR-0061 §2.3) after a successful
/// install. The bot token and signing secret are stored as tenant
/// secrets per <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0003-secret-inheritance-unit-to-tenant.md">ADR-0003</see>;
/// the binding row carries the secret <em>names</em> only — never
/// plaintext credentials.
/// </para>
/// </summary>
/// <param name="TeamId">
/// The Slack workspace id (<c>team.id</c> from the OAuth response).
/// </param>
/// <param name="TeamName">
/// Display name of the Slack workspace, or <c>null</c> when the
/// platform never resolved it.
/// </param>
/// <param name="BotUserId">
/// Slack <c>user_id</c> of the bot identity issued by the install.
/// </param>
/// <param name="BotTokenSecretName">
/// Tenant-secret name (ADR-0003) addressing the bot OAuth access
/// token.
/// </param>
/// <param name="SigningSecretSecretName">
/// Tenant-secret name addressing the Slack app's signing secret.
/// </param>
/// <param name="InstallerUserId">
/// Slack <c>user_id</c> of the OAuth installer — the bound user in
/// OSS v0.1 per ADR-0061 §2.1.
/// </param>
/// <param name="SingleUserMode">
/// ADR-0061 §7.3 — gate for the auto-leave / scope-omission
/// behaviour. Defaults <c>true</c> in OSS; multi-user installs flip
/// to <c>false</c>.
/// </param>
/// <param name="Mode">
/// ADR-0061 §7.6 — <see cref="SlackBindingMode"/>. v0.1 always
/// persists <see cref="SlackBindingMode.Workspace"/>;
/// <see cref="SlackBindingMode.Org"/> is the forward-compat slot for
/// the Enterprise Grid path.
/// </param>
/// <param name="BoundUsers">
/// ADR-0061 §7.1 — list of <c>(slack_user_id, tenant_user_id)</c>
/// mappings. Length 1 in OSS (the OAuth installer mapped to
/// <c>OssTenantUserIds.Operator</c>); length N in cloud.
/// </param>
public sealed record TenantSlackConfig(
    [property: JsonPropertyName("team_id")] string TeamId,
    [property: JsonPropertyName("team_name")] string? TeamName,
    [property: JsonPropertyName("bot_user_id")] string BotUserId,
    [property: JsonPropertyName("bot_token_secret_name")] string BotTokenSecretName,
    [property: JsonPropertyName("signing_secret_secret_name")] string SigningSecretSecretName,
    [property: JsonPropertyName("installer_user_id")] string InstallerUserId,
    [property: JsonPropertyName("single_user_mode")] bool SingleUserMode,
    [property: JsonPropertyName("mode")] SlackBindingMode Mode,
    [property: JsonPropertyName("bound_users")] IReadOnlyList<TenantSlackBoundUser> BoundUsers);

/// <summary>
/// One entry in the Slack binding's bound-user list (ADR-0061 §7.1).
/// Even in OSS the list has length 1; cloud may grow to many.
/// </summary>
/// <param name="SlackUserId">The Slack <c>user_id</c> (opaque to SV).</param>
/// <param name="TenantUserId">The mapped SV <c>TenantUser</c>.</param>
public sealed record TenantSlackBoundUser(
    [property: JsonPropertyName("slack_user_id")] string SlackUserId,
    [property: JsonPropertyName("tenant_user_id")] Guid TenantUserId);

/// <summary>
/// ADR-0061 §7.6 — the Slack binding mode. v0.1 only supports
/// <see cref="Workspace"/>; the <see cref="Org"/> value is the
/// Enterprise Grid forward-compat slot. The OAuth install path
/// refuses Grid bindings; the column exists so the future Grid
/// install lands without a schema change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SlackBindingMode>))]
public enum SlackBindingMode
{
    /// <summary>Standard workspace install (the only supported mode in OSS v0.1).</summary>
    Workspace = 0,

    /// <summary>Enterprise Grid org-level install. Refused at install time in v0.1.</summary>
    Org = 1,
}
