// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Shared constants describing the per-agent workspace mount the platform
/// provisions on every agent container launch (D1 spec § 2.2.1, ADR-0029).
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0055 §5 the workspace mount path is always per-member —
/// <c>/spring/members/&lt;memberId&gt;/</c> — including the single-member
/// "standalone" case. Use <see cref="BuildMountPath"/> at every call site;
/// there is no global "the workspace path" constant.
/// </para>
/// <para>
/// Launcher implementations depend on <c>Cvoya.Spring.Core</c> only and
/// emit the canonical env-var names from this class without taking a
/// dependency on the Dapr-side <c>AgentVolumeManager</c> that owns the
/// container-runtime side. This is the seam ADR-0038 Chunk 2a opened so
/// the per-runtime launchers can live in <c>Cvoya.Spring.AgentRuntimes</c>.
/// </para>
/// </remarks>
public static class AgentWorkspaceContract
{
    /// <summary>Env var name the D1 spec mandates for the workspace mount path.</summary>
    public const string WorkspacePathEnvVar = "SPRING_WORKSPACE_PATH";

    /// <summary>
    /// Env var name carrying the absolute URL of the worker bootstrap
    /// endpoint the agent-sidecar pulls its configuration from
    /// (ADR-0055 §9). Set by the launcher at container launch time.
    /// </summary>
    public const string BootstrapUrlEnvVar = "SPRING_BOOTSTRAP_URL";

    /// <summary>
    /// Env var name carrying the per-agent bootstrap bearer token
    /// (ADR-0055 §8). Lifetime = agent lifetime — issued at agent
    /// provision time, revoked at undeploy. Set by the launcher at
    /// container launch time.
    /// </summary>
    public const string BootstrapTokenEnvVar = "SPRING_BOOTSTRAP_TOKEN";

    /// <summary>
    /// In-container mount path for the per-member workspace volume
    /// (ADR-0055 §5). Includes a trailing slash. Use
    /// <see cref="BuildMountPathNoSlash"/> when composing a path to a
    /// workspace-relative file so the join does not produce a doubled
    /// separator.
    /// </summary>
    public static string BuildMountPath(string memberId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        return $"/spring/members/{memberId}/";
    }

    /// <summary>
    /// <see cref="BuildMountPath"/> without its trailing slash. Compose a
    /// path to a workspace-relative file as
    /// <c>$"{BuildMountPathNoSlash(memberId)}/{file}"</c>.
    /// </summary>
    public static string BuildMountPathNoSlash(string memberId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        return $"/spring/members/{memberId}";
    }
}
