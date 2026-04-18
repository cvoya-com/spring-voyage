// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

/// <summary>
/// Configuration options for the <c>spring-dispatcher</c> service. Bound from the
/// <c>Dispatcher</c> configuration section.
/// </summary>
/// <remarks>
/// The dispatcher is the standalone-deployment process that owns the host
/// container runtime (podman for OSS). Workers reach it over HTTP — they never
/// hold runtime credentials themselves. Authorisation is a per-worker bearer
/// token mapped to a tenant scope; see <see cref="Tokens"/>.
/// </remarks>
public class DispatcherOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dispatcher";

    /// <summary>
    /// Per-worker bearer tokens. Keys are opaque token strings (issued at deploy
    /// time); values carry the tenant id the token is scoped to. Requests whose
    /// bearer token is absent from this map are rejected 401. Requests whose
    /// token is present but mismatched against a tenant scope asserted by the
    /// call site are rejected 403. The OSS single-host deployment typically
    /// issues one token scoped to the default tenant.
    /// </summary>
    public IDictionary<string, DispatcherTokenScope> Tokens { get; set; }
        = new Dictionary<string, DispatcherTokenScope>(StringComparer.Ordinal);
}

/// <summary>
/// Token-to-tenant scope assertion. A request carrying a token whose
/// <see cref="TenantId"/> does not match the request's asserted tenant is
/// rejected 403.
/// </summary>
/// <param name="TenantId">The tenant id the token is scoped to.</param>
public record DispatcherTokenScope(string TenantId);