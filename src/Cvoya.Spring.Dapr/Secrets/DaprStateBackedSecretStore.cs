// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Client;

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
/// </summary>
public class DaprStateBackedSecretStore : ISecretStore
{
    private readonly DaprClient _daprClient;
    private readonly ISecretsEncryptor _encryptor;
    private readonly ITenantContext _tenantContext;
    private readonly IOptions<SecretsOptions> _options;
    private readonly ILogger<DaprStateBackedSecretStore> _logger;

    /// <summary>
    /// Creates a new <see cref="DaprStateBackedSecretStore"/>.
    /// </summary>
    public DaprStateBackedSecretStore(
        DaprClient daprClient,
        ISecretsEncryptor encryptor,
        ITenantContext tenantContext,
        IOptions<SecretsOptions> options,
        ILogger<DaprStateBackedSecretStore> logger)
    {
        _daprClient = daprClient;
        _encryptor = encryptor;
        _tenantContext = tenantContext;
        _options = options;
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
