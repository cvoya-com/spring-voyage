// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Options;

/// <summary>
/// Static, configuration-backed <see cref="ITenantContext"/>. Reads the
/// tenant id from <c>Secrets:DefaultTenantId</c>, defaulting to
/// <see cref="OssTenantIds.Default"/>. This implementation ships with
/// the OSS core; the private cloud repo replaces it with a
/// request-scoped variant that resolves the tenant from the
/// authenticated principal.
/// </summary>
public class ConfiguredTenantContext : ITenantContext
{
    /// <summary>
    /// The tenant id used when <see cref="SecretsOptions.DefaultTenantId"/>
    /// is absent, whitespace, or unparseable. Mirrors
    /// <see cref="OssTenantIds.Default"/>.
    /// </summary>
    public static readonly Guid DefaultTenantId = OssTenantIds.Default;

    /// <summary>
    /// Creates a new <see cref="ConfiguredTenantContext"/>.
    /// </summary>
    /// <param name="options">Bound <see cref="SecretsOptions"/>.</param>
    public ConfiguredTenantContext(IOptions<SecretsOptions> options)
    {
        var configured = options.Value.DefaultTenantId;
        CurrentTenantId = configured != Guid.Empty ? configured : DefaultTenantId;
    }

    /// <inheritdoc />
    public Guid CurrentTenantId { get; }
}
