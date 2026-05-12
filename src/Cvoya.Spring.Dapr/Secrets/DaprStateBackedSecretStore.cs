// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

using System.Text.Json;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// OSS <see cref="ISecretStore"/> implementation backed by the Dapr
/// state-management building block. Values are wrapped in an
/// application-layer AES-GCM envelope (see <see cref="ISecretsEncryptor"/>)
/// before being handed to Dapr so plaintext never lands in the backing
/// state store (Redis in local dev, etc.).
///
/// <para>
/// <b>Component selection.</b> By default all tenants share a single
/// Dapr state store component (<see cref="SecretsOptions.StoreComponent"/>).
/// When <see cref="SecretsOptions.ComponentNameFormat"/> contains
/// <c>{tenantId}</c>, the store resolves to per-tenant components at
/// call time — a misconfigured caller targeting the wrong component
/// cross-reads nothing, and the AES envelope's tenant-bound AAD rejects
/// transplanted ciphertexts if the components were ever swapped.
/// </para>
///
/// <para>
/// <b>Backwards compatibility.</b> Values persisted before at-rest
/// encryption was introduced (plain UTF-8 strings without the version
/// byte) are readable as-is and are re-enveloped on the next write.
/// </para>
///
/// <para>
/// <b>#2212 migration fallback.</b> Earlier deployments routed the secret
/// store through the shared <c>statestore</c> Dapr component, which
/// uses <c>keyPrefix: appid</c>. Secrets written by <c>spring-api</c>
/// landed at <c>spring-api||secrets/{key}</c> in <c>public.state</c> and
/// were invisible to <c>spring-worker</c> reads. The store now defaults
/// to the dedicated <c>secretsstore</c> component (<c>keyPrefix: none</c>);
/// when a read misses the canonical key the store falls back to a
/// direct PostgreSQL lookup of the API-legacy row so existing secrets
/// stay resolvable without operator-led re-entry. The next rotation /
/// re-write moves the value to the canonical key.
/// </para>
/// </summary>
public class DaprStateBackedSecretStore : ISecretStore
{
    // App-id used by the API host before #2212 split the secrets backend
    // out of the shared `statestore` component. Secrets written under
    // this app-id are still stored in `public.state` under
    // `spring-api||secrets/{key}`; the read path falls back to a direct
    // PostgreSQL lookup so existing deployments continue to resolve
    // credentials after the upgrade without operator-led re-entry.
    private const string LegacyApiAppId = "spring-api";

    private readonly DaprClient _daprClient;
    private readonly ISecretsEncryptor _encryptor;
    private readonly ITenantContext _tenantContext;
    private readonly IOptions<SecretsOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DaprStateBackedSecretStore> _logger;

    /// <summary>
    /// Creates a new <see cref="DaprStateBackedSecretStore"/>.
    /// </summary>
    public DaprStateBackedSecretStore(
        DaprClient daprClient,
        ISecretsEncryptor encryptor,
        ITenantContext tenantContext,
        IOptions<SecretsOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<DaprStateBackedSecretStore> logger)
    {
        _daprClient = daprClient;
        _encryptor = encryptor;
        _tenantContext = tenantContext;
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> WriteAsync(string plaintext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var storeKey = Guid.NewGuid().ToString("N");
        var tenantId = _tenantContext.CurrentTenantId;
        var component = ResolveComponent(tenantId);
        var backendKey = BuildBackendKey(storeKey);

        var envelope = _encryptor.Encrypt(plaintext, tenantId, storeKey);

        _logger.LogDebug(new EventId(2400, "SecretWriteStarted"),
            "Writing secret to component {Component} under backend key {BackendKey}",
            component, backendKey);

        await _daprClient.SaveStateAsync(
            component,
            backendKey,
            envelope,
            cancellationToken: ct);

        _logger.LogDebug(new EventId(2401, "SecretWriteCompleted"),
            "Wrote secret under backend key {BackendKey}", backendKey);

        return storeKey;
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(string storeKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        var tenantId = _tenantContext.CurrentTenantId;
        var component = ResolveComponent(tenantId);
        var backendKey = BuildBackendKey(storeKey);

        _logger.LogDebug(new EventId(2402, "SecretReadStarted"),
            "Reading secret from component {Component} under backend key {BackendKey}",
            component, backendKey);

        var stored = await _daprClient.GetStateAsync<string?>(
            component,
            backendKey,
            cancellationToken: ct);

        if (string.IsNullOrEmpty(stored))
        {
            // Legacy fallback: older deployments may have used a backend
            // key that embedded the tenant id. Try that shape once before
            // giving up. A one-time rewrite is only needed if operators
            // flip ComponentNameFormat on existing data — see docs.
            var legacyKey = BuildLegacyBackendKey(tenantId, storeKey);
            if (legacyKey != backendKey)
            {
                stored = await _daprClient.GetStateAsync<string?>(
                    component,
                    legacyKey,
                    cancellationToken: ct);

                if (!string.IsNullOrEmpty(stored))
                {
                    _logger.LogDebug(new EventId(2406, "SecretLegacyKeyHit"),
                        "Read secret from legacy tenant-prefixed backend key {LegacyKey}", legacyKey);
                }
            }
        }

        if (string.IsNullOrEmpty(stored))
        {
            // #2212 migration fallback: secrets written by the API host
            // before the dedicated `secretsstore` component (with
            // `keyPrefix: none`) was introduced live in `public.state`
            // under `spring-api||secrets/{key}`. Dapr's client transparently
            // re-prefixes keys with the caller's own app-id, so we cannot
            // read the API-namespaced row through the worker's sidecar —
            // we go directly to PostgreSQL for the legacy lookup.
            stored = await TryReadLegacyApiKeyAsync(storeKey, ct);

            if (!string.IsNullOrEmpty(stored))
            {
                _logger.LogDebug(new EventId(2407, "SecretApiLegacyKeyHit"),
                    "Read secret from API-legacy backend key for store key {StoreKey}", storeKey);
            }
        }

        if (string.IsNullOrEmpty(stored))
        {
            _logger.LogDebug(new EventId(2403, "SecretReadCompleted"),
                "Read secret under backend key {BackendKey}; found: false", backendKey);
            return null;
        }

        var plaintext = _encryptor.Decrypt(stored, tenantId, storeKey, out var wasEnveloped);

        _logger.LogDebug(new EventId(2403, "SecretReadCompleted"),
            "Read secret under backend key {BackendKey}; found: true; enveloped: {Enveloped}",
            backendKey, wasEnveloped);

        return plaintext;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string storeKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        var tenantId = _tenantContext.CurrentTenantId;
        var component = ResolveComponent(tenantId);
        var backendKey = BuildBackendKey(storeKey);

        _logger.LogDebug(new EventId(2404, "SecretDeleteStarted"),
            "Deleting secret from component {Component} under backend key {BackendKey}",
            component, backendKey);

        await _daprClient.DeleteStateAsync(
            component,
            backendKey,
            cancellationToken: ct);

        // Best-effort legacy cleanup so the row doesn't linger after a
        // format migration. Missing keys are not an error for Dapr state.
        var legacyKey = BuildLegacyBackendKey(tenantId, storeKey);
        if (legacyKey != backendKey)
        {
            await _daprClient.DeleteStateAsync(
                component,
                legacyKey,
                cancellationToken: ct);
        }

        _logger.LogDebug(new EventId(2405, "SecretDeleteCompleted"),
            "Deleted secret under backend key {BackendKey}", backendKey);
    }

    // The canonical backend key is KeyPrefix + opaque storeKey. Tenant
    // correlation lives in the registry (ISecretRegistry); the key
    // carries no structural metadata.
    private string BuildBackendKey(string storeKey) =>
        $"{_options.Value.KeyPrefix}{storeKey}";

    // Legacy backend-key shape that older deployments may have used.
    // Kept only for read-path fallback so operators who enable per-tenant
    // component isolation on existing data don't need to migrate rows
    // eagerly — the next write re-enveloped them under the canonical key.
    private string BuildLegacyBackendKey(Guid tenantId, string storeKey) =>
        $"{_options.Value.KeyPrefix}{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(tenantId)}/{storeKey}";

    // #2212 legacy key shape: secrets written by spring-api before the
    // dedicated `secretsstore` component existed are in `public.state`
    // under `<app-id>||<canonical-backend-key>`. The Dapr PostgreSQL
    // state store always uses `||` as the appid/key separator regardless
    // of the caller's `keyPrefix` setting — see Dapr's PG component
    // source.
    private string BuildApiLegacyBackendKey(string storeKey) =>
        $"{LegacyApiAppId}||{_options.Value.KeyPrefix}{storeKey}";

    private async Task<string?> TryReadLegacyApiKeyAsync(string storeKey, CancellationToken ct)
    {
        var legacyKey = BuildApiLegacyBackendKey(storeKey);

        // The Dapr SDK silently re-prefixes keys with the caller's own
        // app-id, so we cannot read a `spring-api||...` key through the
        // worker's sidecar — go directly to PostgreSQL. We use the
        // SpringDbContext's underlying connection so we inherit the
        // already-validated connection string and connection pooling.
        // The store is a singleton so we open a per-call DI scope.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        if (!db.Database.IsRelational())
        {
            // Test harnesses use the EF in-memory provider; they never
            // populate `public.state` so the fallback would fail with a
            // provider exception. The pre-existing tests cover the normal
            // read path — return null and let the caller report "not
            // found" without surfacing an infrastructure error.
            return null;
        }

        var conn = db.Database.GetDbConnection();
        var opened = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                opened = true;
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM public.state WHERE key = @key";
            var p = cmd.CreateParameter();
            p.ParameterName = "@key";
            p.Value = legacyKey;
            cmd.Parameters.Add(p);

            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null || raw is DBNull)
            {
                return null;
            }

            // Dapr stores state as a JSON value in the JSONB `value`
            // column. Strings are written as JSON strings (i.e. the
            // payload is the literal value wrapped in double quotes,
            // with embedded quotes escaped). Decode it back to a raw
            // .NET string so the encryptor sees the same envelope it
            // produced at write time.
            var text = raw.ToString();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            try
            {
                var decoded = JsonSerializer.Deserialize<string?>(text);
                return string.IsNullOrEmpty(decoded) ? null : decoded;
            }
            catch (JsonException)
            {
                // Defensive fallback: if a row was ever written
                // outside the Dapr code path (e.g. a manual seed) the
                // value column may not be JSON-encoded. Return the raw
                // text and let the encryptor treat it as legacy
                // plaintext.
                return text;
            }
        }
        catch (Exception ex)
        {
            // The legacy fallback is best-effort by design; an
            // infrastructure failure here should not mask the
            // canonical-path miss with a different error class. Log at
            // warning so operators can spot persistent failures.
            _logger.LogWarning(new EventId(2408, "SecretApiLegacyReadFailed"),
                ex,
                "Failed to read API-legacy secret row for store key {StoreKey}", storeKey);
            return null;
        }
        finally
        {
            if (opened && conn.State == System.Data.ConnectionState.Open)
            {
                await conn.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private string ResolveComponent(Guid tenantId)
    {
        var format = _options.Value.ComponentNameFormat;
        if (string.IsNullOrWhiteSpace(format))
        {
            return _options.Value.StoreComponent;
        }

        return format.Replace("{tenantId}", Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(tenantId), StringComparison.Ordinal);
    }
}
