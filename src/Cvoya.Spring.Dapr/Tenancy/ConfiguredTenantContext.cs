// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Options;

/// <summary>
/// Static, configuration-backed <see cref="ITenantContext"/>. Reads the
/// tenant id from <c>Secrets:DefaultTenantId</c>, defaulting to
/// <c>"default"</c>. This implementation ships with the OSS core; the
/// private cloud repo replaces it with a request-scoped variant that
/// resolves the tenant from the authenticated principal.
/// </summary>
public class ConfiguredTenantContext : ITenantContext
{
    /// <summary>
    /// The tenant id used when <see cref="SecretsOptions.DefaultTenantId"/>
    /// is absent or whitespace. The OSS platform ships functionally
    /// single-tenant, and <c>"default"</c> is the canonical tenant name
    /// for the singleton install. Kept as a constant so the bootstrap /
    /// migration paths can reference it without drifting from the
    /// fallback used here.
    /// </summary>
    public const string DefaultTenantId = "default";

    /// <summary>
    /// Creates a new <see cref="ConfiguredTenantContext"/>.
    /// </summary>
    /// <param name="options">Bound <see cref="SecretsOptions"/>.</param>
    public ConfiguredTenantContext(IOptions<SecretsOptions> options)
    {
        var configured = options.Value.DefaultTenantId;
        CurrentTenantId = string.IsNullOrWhiteSpace(configured) ? DefaultTenantId : configured;
    }

    /// <inheritdoc />
    public string CurrentTenantId { get; }
}