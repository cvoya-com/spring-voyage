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

    /// <summary>Default value for <see cref="WorkspaceRoot"/>.</summary>
    public const string DefaultWorkspaceRoot = "/var/lib/spring-workspaces";

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

    /// <summary>
    /// Filesystem root the dispatcher uses for per-invocation agent
    /// workspaces. Each <c>POST /v1/containers</c> with a workspace request
    /// gets a unique subdirectory here, which the dispatcher then bind-mounts
    /// into the agent container. The directory must exist on the dispatcher
    /// process's filesystem AND be addressable by the host's container runtime
    /// (the dispatcher's <c>podman</c> shells out against the host socket).
    /// Defaults to <see cref="DefaultWorkspaceRoot"/>; the deployment scripts
    /// pre-create this directory and bind-mount it into the dispatcher
    /// container at the same path. See issue #1042.
    /// </summary>
    public string WorkspaceRoot { get; set; } = DefaultWorkspaceRoot;
}

/// <summary>
/// Token-to-tenant scope assertion. A request carrying a token whose
/// <see cref="TenantId"/> does not match the request's asserted tenant is
/// rejected 403.
/// </summary>
/// <param name="TenantId">The tenant id the token is scoped to.</param>
public record DispatcherTokenScope(string TenantId);