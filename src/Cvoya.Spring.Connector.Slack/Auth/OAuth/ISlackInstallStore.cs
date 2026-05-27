// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

/// <summary>
/// Connector-side facade for persisting and removing the Slack tenant
/// binding plus its tenant secrets. Owns the tenant-scoped
/// transactional shape so the OAuth service stays free of EF /
/// secret-store specifics.
/// </summary>
/// <remarks>
/// The <c>team_id ↔ tenant</c> cross-tenant lookup the inbound webhook
/// handler needs (ADR-0061 §7.5) is served by the generic
/// <c>ITenantConnectorBindingStore.GetByExternalIdentityAsync</c> path —
/// the binding row carries the <c>team_id</c> on its
/// <c>external_identity</c> column, indexed unique across tenants.
/// </remarks>
public interface ISlackInstallStore
{
    /// <summary>
    /// Returns the existing Slack binding for the current tenant, or
    /// <c>null</c> when the tenant is not bound. The returned snapshot
    /// carries just enough to support the conflict-detection (ADR-0061
    /// §2.5) and disconnect (ADR-0061 §2.5) paths — including the
    /// secret names so the revoke + cleanup steps know what to touch.
    /// </summary>
    Task<SlackBindingSnapshot?> GetExistingBindingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cross-tenant lookup: returns the Slack binding that claims
    /// <paramref name="teamId"/>, regardless of which tenant owns it,
    /// or <c>null</c> when no tenant is bound to that workspace. Backs
    /// inbound-webhook routing where the delivery carries only the
    /// Slack <c>team_id</c> (ADR-0061 §7.5).
    /// </summary>
    Task<SlackBindingSnapshot?> GetByTeamIdAsync(string teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the bot OAuth access token out of the secret store via
    /// the secret name persisted on the binding row.
    /// </summary>
    Task<string?> ReadBotTokenAsync(SlackBindingSnapshot binding, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a fresh Slack install — writes the bot token + signing
    /// secret to the tenant secret store and upserts the tenant
    /// binding row. The Slack <c>team_id</c> is stored on the binding's
    /// <c>external_identity</c> column, indexed cross-tenant so a
    /// second tenant cannot claim the same workspace (ADR-0061 §7.5).
    /// </summary>
    Task PersistInstallAsync(SlackInstallPayload payload, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the tenant binding row (which also frees the
    /// <c>team_id</c> slot on the cross-tenant index) and the stored
    /// tenant secrets for <paramref name="binding"/>. Idempotent.
    /// </summary>
    Task DeleteInstallAsync(SlackBindingSnapshot binding, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal projection of the persisted Slack binding state needed by
/// the OAuth service's conflict-detection and disconnect paths.
/// </summary>
/// <param name="TeamId">The Slack <c>team_id</c> pinned on the binding.</param>
/// <param name="BotTokenSecretName">Tenant secret name holding the bot OAuth token.</param>
/// <param name="SigningSecretSecretName">Tenant secret name holding the signing secret.</param>
public record SlackBindingSnapshot(
    string TeamId,
    string BotTokenSecretName,
    string SigningSecretSecretName);

/// <summary>
/// All data the OAuth callback needs to persist a fresh Slack
/// install. Carries plaintext secrets — the install store writes them
/// to the secret store, then drops them from memory before the
/// binding row is written.
/// </summary>
/// <param name="TeamId">Slack workspace id from the OAuth response.</param>
/// <param name="TeamName">Workspace display name (optional).</param>
/// <param name="BotUserId">Slack user_id of the bot identity.</param>
/// <param name="BotAccessToken">Bot OAuth access token (plaintext).</param>
/// <param name="SigningSecret">Slack app signing secret (plaintext).</param>
/// <param name="InstallerUserId">Slack user_id of the OAuth installer.</param>
/// <param name="EnterpriseId">Enterprise id (always null in v0.1 — Grid is refused upstream).</param>
public record SlackInstallPayload(
    string TeamId,
    string? TeamName,
    string BotUserId,
    string BotAccessToken,
    string SigningSecret,
    string InstallerUserId,
    string? EnterpriseId);
