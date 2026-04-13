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
    /// <see cref="Cvoya.Spring.Dapr.Secrets.DaprStateBackedSecretStore"/>.
    /// Defaults to the platform's shared <c>statestore</c> component.
    /// </summary>
    public string StoreComponent { get; set; } = "statestore";

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
}