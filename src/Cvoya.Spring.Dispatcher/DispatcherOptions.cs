// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

/// <summary>
/// Configuration options for the <c>spring-dispatcher</c> service. Bound from the
/// <c>Dispatcher</c> configuration section.
/// </summary>
/// <remarks>
/// The dispatcher is the host-process service that owns the host container
/// runtime (podman for OSS). Workers reach it over HTTP — they never hold
/// runtime credentials themselves. Authorisation is a per-worker bearer token
/// mapped to a tenant scope; see <see cref="Tokens"/>. The dispatcher runs as
/// a long-lived host process across Linux/macOS/Windows (issue #1063); it is
/// no longer packaged as a container in the OSS deployment, so its defaults
/// resolve against the user's home directory rather than a fixed Linux FHS
/// path.
/// </remarks>
public class DispatcherOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dispatcher";

    /// <summary>
    /// Default value for <see cref="WorkspaceRoot"/>. Resolved at type init
    /// against <see cref="Environment.SpecialFolder.UserProfile"/> so the
    /// dispatcher works out-of-the-box on every dev machine without root.
    /// Falls back to <c>/var/lib/spring-workspaces</c> on Unix and
    /// <c>%TEMP%/spring-voyage/workspaces</c> on Windows when the user
    /// profile cannot be resolved (e.g. some service-account contexts).
    /// </summary>
    public static readonly string DefaultWorkspaceRoot = ResolveDefaultWorkspaceRoot();

    private static string ResolveDefaultWorkspaceRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            return Path.Combine(home, ".spring-voyage", "workspaces");
        }

        return OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), "spring-voyage", "workspaces")
            : "/var/lib/spring-workspaces";
    }

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
    /// (the dispatcher invokes podman against the host socket directly because
    /// the dispatcher itself is a host process — issue #1063). Defaults to
    /// <see cref="DefaultWorkspaceRoot"/>; <c>spring-voyage-host.sh</c>
    /// pre-creates this directory before launching the dispatcher. See issue
    /// #1042.
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