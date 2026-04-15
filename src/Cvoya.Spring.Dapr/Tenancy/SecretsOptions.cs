// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

/// <summary>
/// Configuration options bound from the <c>Secrets</c> configuration
/// section. Controls the default tenant id used by
/// <see cref="ConfiguredTenantContext"/> and the two feature flags that
/// gate the unit-scoped secrets HTTP API.
/// </summary>
public class SecretsOptions
{
    /// <summary>
    /// The configuration section name used for binding.
    /// </summary>
    public const string SectionName = "Secrets";

    /// <summary>
    /// The tenant id returned by the OSS <see cref="ConfiguredTenantContext"/>.
    /// Defaults to <c>"local"</c>.
    /// </summary>
    public string DefaultTenantId { get; set; } = "local";

    /// <summary>
    /// Whether the API accepts <c>{ name, value }</c> (pass-through) writes
    /// that store plaintext via the server-side <see cref="Cvoya.Spring.Core.Secrets.ISecretStore"/>.
    /// </summary>
    public bool AllowPassThroughWrites { get; set; } = true;

    /// <summary>
    /// Whether the API accepts <c>{ name, externalStoreKey }</c> writes that
    /// bind a secret name to an externally-managed store key (e.g. a
    /// Key Vault reference).
    /// </summary>
    public bool AllowExternalReferenceWrites { get; set; } = true;

    /// <summary>
    /// The Dapr state store component name used by the OSS
    /// <see cref="Cvoya.Spring.Dapr.Secrets.DaprStateBackedSecretStore"/>
    /// when <see cref="ComponentNameFormat"/> is not set. Defaults to the
    /// platform's shared <c>statestore</c> component.
    /// </summary>
    public string StoreComponent { get; set; } = "statestore";

    /// <summary>
    /// Optional per-tenant Dapr component-name template. When set, the
    /// store resolves the backing component at call time by substituting
    /// <c>{tenantId}</c> in this string — e.g. <c>"statestore-{tenantId}"</c>
    /// means tenant <c>acme</c> uses the Dapr component
    /// <c>statestore-acme</c>. When <c>null</c> or empty (the OSS default),
    /// the single shared <see cref="StoreComponent"/> is used and tenant
    /// isolation is enforced structurally by the registry. The private
    /// cloud deployment sets this to achieve per-tenant secret-store
    /// isolation defense in depth.
    /// </summary>
    public string? ComponentNameFormat { get; set; }

    /// <summary>
    /// Optional filesystem path to a file whose contents are a
    /// base64-encoded 32-byte AES-256 key used by
    /// <see cref="Cvoya.Spring.Dapr.Secrets.SecretsEncryptor"/>. Useful
    /// for container deployments that mount a key file rather than pass a
    /// key through an environment variable. The <c>SPRING_SECRETS_AES_KEY</c>
    /// environment variable takes priority when both are present.
    /// </summary>
    public string? AesKeyFile { get; set; }

    /// <summary>
    /// When <c>true</c> and no key is configured via
    /// <c>SPRING_SECRETS_AES_KEY</c> or <see cref="AesKeyFile"/>, the OSS
    /// encryptor generates a random key in memory at startup and logs a
    /// warning. Intended to keep <c>dotnet run</c> frictionless in dev;
    /// any encrypted values become unreadable after a restart. Defaults
    /// to <c>false</c> so production misconfiguration fails fast.
    /// </summary>
    public bool AllowEphemeralDevKey { get; set; }

    /// <summary>
    /// The key prefix used by the OSS secret store inside the shared Dapr
    /// state store component. Keeps secret keys visually distinct from
    /// other state keys (budgets, policies, etc.) that share the same
    /// component.
    /// </summary>
    public string KeyPrefix { get; set; } = "secrets/";

    /// <summary>
    /// The maximum number of secrets allowed per scope owner (e.g. per
    /// unit). A <c>POST</c> that would push the count above this limit
    /// is rejected with 429. Set to 0 to disable the limit.
    /// </summary>
    public int MaxSecretsPerOwner { get; set; } = 100;

    /// <summary>
    /// Whether <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/>
    /// falls through from <see cref="Cvoya.Spring.Core.Secrets.SecretScope.Unit"/>
    /// to <see cref="Cvoya.Spring.Core.Secrets.SecretScope.Tenant"/>
    /// when the unit-scoped entry is missing. Defaults to <c>true</c>:
    /// tenant-wide secrets (e.g. a shared CI token) are visible from
    /// unit context without per-unit duplication. Set to <c>false</c>
    /// to require strict scope isolation — unit resolves that miss will
    /// return <c>null</c> even when a same-name tenant entry exists.
    ///
    /// <para>
    /// The fall-through path still consults
    /// <see cref="Cvoya.Spring.Core.Secrets.ISecretAccessPolicy"/> at
    /// BOTH scopes with the <see cref="Cvoya.Spring.Core.Secrets.SecretAccessAction.Read"/>
    /// action; a caller with only a unit read grant cannot obtain a
    /// tenant-scoped value without a separate tenant grant.
    /// </para>
    /// </summary>
    public bool InheritTenantFromUnit { get; set; } = true;

    /// <summary>
    /// Count-based retention for secret versions under the wave 7 A5
    /// multi-version-coexistence scheme. <c>0</c> (the default) means
    /// UNBOUNDED retention: every rotation appends a new version and
    /// nothing is pruned automatically. A positive value represents the
    /// maximum number of versions to retain per
    /// <see cref="Cvoya.Spring.Core.Secrets.SecretRef"/>; operators may
    /// consult this value when invoking <c>POST /.../secrets/{name}/prune</c>
    /// or when building their own retention-enforcement scheduler (see
    /// follow-up issue for the built-in scheduler).
    ///
    /// <para>
    /// The OSS registry never applies this value automatically — it is
    /// a configuration signal, not an enforcement hook. A future wave
    /// will add a background service that polls this setting and
    /// invokes <c>PruneAsync</c> per chain; until then the value is
    /// documentary and operators must call prune themselves or wire
    /// their own scheduler.
    /// </para>
    /// </summary>
    public int VersionRetention { get; set; }
}