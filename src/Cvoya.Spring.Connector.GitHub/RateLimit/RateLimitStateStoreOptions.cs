// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.RateLimit;

/// <summary>
/// Options controlling <see cref="IRateLimitStateStore"/> selection and
/// placement. Bound from the <c>GitHub:RateLimit:StateStore</c>
/// configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backend selection.</b> <see cref="Backend"/> picks between the OSS
/// in-memory store (default, single-host) and the Dapr state-store-backed
/// store (multi-host). Private deployments override via
/// <c>services.TryAddSingleton&lt;IRateLimitStateStore, ...&gt;()</c>
/// before calling <c>AddCvoyaSpringConnectorGitHub</c>.
/// </para>
/// <para>
/// <b>Default installation key.</b> The rate-limit tracker is frequently
/// consulted before a specific installation is known (e.g. the app-JWT
/// probe that lists installations). <see cref="DefaultInstallationKey"/>
/// is used as the scope key for those calls so state is never lost to
/// an empty key.
/// </para>
/// </remarks>
public sealed class RateLimitStateStoreOptions
{
    /// <summary>Backend identifier — <c>memory</c> (default) or <c>dapr</c>.</summary>
    public string Backend { get; set; } = "memory";

    /// <summary>
    /// Dapr state store component name. Only used when
    /// <see cref="Backend"/> is <c>dapr</c>. The OSS default is the
    /// shared <c>statestore</c> component; override per-tenant via
    /// <see cref="ComponentNameFormat"/> if the private repo opts into
    /// per-tenant isolation.
    /// </summary>
    public string StoreComponent { get; set; } = "statestore";

    /// <summary>
    /// Optional Dapr component-name template — when set, the store
    /// resolves the backing component at call time by substituting
    /// <c>{installationKey}</c>. Mirrors the pattern used by
    /// <c>SecretsOptions.ComponentNameFormat</c>. Rate-limit state does
    /// not need tenant isolation but the convention is supported for
    /// deployments that prefer one component per installation.
    /// </summary>
    public string? ComponentNameFormat { get; set; }

    /// <summary>
    /// Key prefix applied in front of <c>gh-ratelimit/{installationKey}/{resource}</c>.
    /// Keeps rate-limit keys visually distinct from other state keys
    /// (secrets, etc.) that share the same component.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Installation key used when the tracker is consulted outside an
    /// installation-scoped call (e.g. the app-JWT probe). Defaults to
    /// <c>_default</c>.
    /// </summary>
    public string DefaultInstallationKey { get; set; } = "_default";
}