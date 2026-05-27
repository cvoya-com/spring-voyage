// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISlackInstallStore"/> implementation. The Slack
/// connector is the only consumer of this surface today; future
/// workspace-shaped connectors that need a cross-tenant team-id-like
/// lookup reuse the same
/// <see cref="ITenantConnectorBindingStore.GetByExternalIdentityAsync"/>
/// surface — no per-connector storage code needed.
///
/// <para>
/// Wraps:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ITenantConnectorBindingStore"/> — the per-tenant binding row, including the <c>external_identity</c> (the Slack <c>team_id</c>) used for cross-tenant inbound-webhook routing.</description></item>
///   <item><description><see cref="ISecretStore"/> + <see cref="ISecretRegistry"/> — bot token + signing secret persistence per ADR-0003.</description></item>
/// </list>
///
/// <para>
/// The class is registered as a singleton so the OAuth service (also
/// a singleton) can take a constant dependency. Scoped peers
/// (<see cref="ISecretStore"/>, <see cref="ISecretRegistry"/>,
/// <see cref="ITenantContext"/>) are resolved through an
/// <see cref="IServiceScopeFactory"/> per call — the same
/// singleton-safety pattern the GitHub connector's
/// <c>OAuthTokenPersister</c> uses.
/// </para>
///
/// <para>
/// CONVENTIONS §16: this connector references
/// <c>Cvoya.Spring.Connectors.Abstractions</c> only. Cross-tenant
/// <c>team_id → tenant</c> resolution previously sat in a
/// Slack-specific EF table behind a Dapr reference; the cleanup in
/// the slack-foundation series moved it onto the generic
/// <c>tenant_connector_bindings.external_identity</c> column so the
/// connector talks only to the Abstractions surface.
/// </para>
/// </summary>
public class SlackInstallStore : ISlackInstallStore
{
    /// <summary>The connector slug Slack binding rows are keyed by.</summary>
    public const string ConnectorSlug = "slack";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlackInstallStore> _logger;

    /// <summary>Creates a new <see cref="SlackInstallStore"/>.</summary>
    public SlackInstallStore(
        IServiceScopeFactory scopeFactory,
        ILogger<SlackInstallStore> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SlackBindingSnapshot?> GetExistingBindingAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var bindingStore = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();

        var binding = await bindingStore.GetAsync(ConnectorSlug, cancellationToken);
        if (binding is null)
        {
            return null;
        }

        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions)
            ?? throw new InvalidOperationException(
                "Slack binding config payload is not TenantSlackConfig-shaped.");

        return new SlackBindingSnapshot(
            TeamId: config.TeamId,
            BotTokenSecretName: config.BotTokenSecretName,
            SigningSecretSecretName: config.SigningSecretSecretName);
    }

    /// <inheritdoc />
    public async Task<SlackBindingSnapshot?> GetByTeamIdAsync(string teamId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var bindingStore = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingStore>();

        // Cross-tenant lookup: an inbound Slack delivery arrives with a
        // team_id and no tenant context. The binding store's
        // (connector_slug, external_identity) index resolves it to the
        // single binding row that claims the workspace.
        var binding = await bindingStore.GetByExternalIdentityAsync(ConnectorSlug, teamId, cancellationToken);
        if (binding is null)
        {
            return null;
        }

        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions)
            ?? throw new InvalidOperationException(
                "Slack binding config payload is not TenantSlackConfig-shaped.");

        return new SlackBindingSnapshot(
            TeamId: config.TeamId,
            BotTokenSecretName: config.BotTokenSecretName,
            SigningSecretSecretName: config.SigningSecretSecretName);
    }

    /// <inheritdoc />
    public async Task<string?> ReadBotTokenAsync(
        SlackBindingSnapshot binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await using var scope = _scopeFactory.CreateAsyncScope();
        return await ReadTenantSecretAsync(scope.ServiceProvider, binding.BotTokenSecretName, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PersistInstallAsync(SlackInstallPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.TeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.BotAccessToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var tenantContext = sp.GetRequiredService<ITenantContext>();
        var bindingStore = sp.GetRequiredService<ITenantConnectorBindingStore>();

        var tenantId = tenantContext.CurrentTenantId;

        // Names — keyed off the team_id so the secret-name surface
        // round-trips deterministically across reinstalls. ADR-0003:
        // the binding row holds names; values live in the secret store.
        var botTokenSecretName = ConnectorSlug + "/" + payload.TeamId + "/bot-token";
        var signingSecretSecretName = ConnectorSlug + "/" + payload.TeamId + "/signing-secret";

        // Write the plaintext values to the store + registry.
        await WriteTenantSecretAsync(sp, tenantId, botTokenSecretName, payload.BotAccessToken, cancellationToken);
        await WriteTenantSecretAsync(sp, tenantId, signingSecretSecretName, payload.SigningSecret, cancellationToken);

        // Build the connector config payload.
        var config = new TenantSlackConfig(
            TeamId: payload.TeamId,
            TeamName: payload.TeamName,
            BotUserId: payload.BotUserId,
            BotTokenSecretName: botTokenSecretName,
            SigningSecretSecretName: signingSecretSecretName,
            InstallerUserId: payload.InstallerUserId,
            SingleUserMode: true,
            Mode: SlackBindingMode.Workspace,
            BoundUsers: new[]
            {
                new TenantSlackBoundUser(
                    SlackUserId: payload.InstallerUserId,
                    TenantUserId: OssTenantUserIds.Operator),
            });

        var configJson = JsonSerializer.SerializeToElement(config, JsonOptions);

        // Upsert the binding row. The team_id rides on the
        // external_identity column so the cross-tenant unique index
        // refuses an install from a second tenant claiming the same
        // workspace (ADR-0061 §7.5 invariant preserved).
        await bindingStore.SetAsync(
            ConnectorSlug,
            SlackConnectorType.SlackTypeId,
            configJson,
            externalIdentity: payload.TeamId,
            cancellationToken);

        _logger.LogInformation(
            "Persisted Slack install for tenant {TenantId} (team_id={TeamId}, bot_user_id={BotUserId})",
            tenantId, payload.TeamId, payload.BotUserId);
    }

    /// <inheritdoc />
    public async Task DeleteInstallAsync(SlackBindingSnapshot binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var bindingStore = sp.GetRequiredService<ITenantConnectorBindingStore>();

        // 1. Binding row + the connector's runtime metadata. The row
        //    carries the external_identity (team_id) directly; deleting
        //    the row frees both the per-tenant slot and the cross-tenant
        //    team_id slot atomically.
        await bindingStore.ClearAsync(ConnectorSlug, cancellationToken);

        // 2. Tenant secrets. The registry rows must come first (they
        //    point at store keys); the store rows are deleted after so
        //    a partial failure leaves dangling registry entries rather
        //    than an unreachable opaque blob.
        await DeleteTenantSecretAsync(sp, binding.BotTokenSecretName, cancellationToken);
        await DeleteTenantSecretAsync(sp, binding.SigningSecretSecretName, cancellationToken);
    }

    private static async Task WriteTenantSecretAsync(
        IServiceProvider sp,
        Guid tenantId,
        string secretName,
        string plaintext,
        CancellationToken cancellationToken)
    {
        var secretStore = sp.GetRequiredService<ISecretStore>();
        var secretRegistry = sp.GetRequiredService<ISecretRegistry>();

        var storeKey = await secretStore.WriteAsync(plaintext, cancellationToken);

        var secretRef = new SecretRef(SecretScope.Tenant, tenantId, secretName);
        await secretRegistry.RegisterAsync(
            secretRef,
            storeKey,
            SecretOrigin.PlatformOwned,
            cancellationToken);
    }

    private static async Task<string?> ReadTenantSecretAsync(
        IServiceProvider sp,
        string secretName,
        CancellationToken cancellationToken)
    {
        var resolver = sp.GetRequiredService<ISecretResolver>();
        var tenantContext = sp.GetRequiredService<ITenantContext>();

        var resolution = await resolver.ResolveWithPathAsync(
            new SecretRef(SecretScope.Tenant, tenantContext.CurrentTenantId, secretName),
            cancellationToken);

        return resolution.Value;
    }

    private static async Task DeleteTenantSecretAsync(
        IServiceProvider sp,
        string secretName,
        CancellationToken cancellationToken)
    {
        var registry = sp.GetRequiredService<ISecretRegistry>();
        var store = sp.GetRequiredService<ISecretStore>();
        var tenantContext = sp.GetRequiredService<ITenantContext>();

        var secretRef = new SecretRef(SecretScope.Tenant, tenantContext.CurrentTenantId, secretName);

        // Fetch the current store key before removing the registry
        // row so we can clean up the store-side blob too. In OSS each
        // binding has exactly one version per name; cloud rotation
        // would have to extend this path. A missing registry row is
        // treated as "already gone" — idempotent disconnect.
        var storeKey = await registry.LookupStoreKeyAsync(secretRef, cancellationToken);
        if (!string.IsNullOrEmpty(storeKey))
        {
            await store.DeleteAsync(storeKey, cancellationToken);
        }

        await registry.DeleteAsync(secretRef, cancellationToken);
    }
}
