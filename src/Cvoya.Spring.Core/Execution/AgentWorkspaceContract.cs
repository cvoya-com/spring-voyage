// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Shared constants describing the per-agent workspace mount the dispatcher
/// provisions on every agent container launch (D1 spec § 2.2.1, ADR-0029).
/// </summary>
/// <remarks>
/// <para>
/// The values live in <c>Cvoya.Spring.Core</c> so launcher implementations
/// (which depend on Core only) can emit the canonical env-var name and
/// mount path without taking a dependency on the Dapr-side
/// <c>AgentVolumeManager</c> that owns the container-runtime side. This is
/// the seam ADR-0038 Chunk 2a opens so the per-runtime launcher classes
/// can move into <c>Cvoya.Spring.AgentRuntimes</c> without dragging
/// Dapr-specific implementation types behind them.
/// </para>
/// <para>
/// The Dapr-side <c>AgentVolumeManager</c> re-exports these constants so
/// existing call sites continue to compile.
/// </para>
/// </remarks>
public static class AgentWorkspaceContract
{
    /// <summary>
    /// Canonical mount path inside every agent container. Matches the
    /// <c>SPRING_WORKSPACE_PATH</c> env var value the launchers set and the
    /// recommended default from the D1 spec (§ 2.1 and § 3.1).
    /// </summary>
    /// <remarks>
    /// Carries a trailing slash. When composing a path to a workspace-relative
    /// file (e.g. <c>.mcp.json</c>) use <see cref="WorkspaceMountPathNoSlash"/>
    /// so the join does not produce a doubled separator.
    /// </remarks>
    public const string WorkspaceMountPath = "/spring/workspace/";

    /// <summary>
    /// <see cref="WorkspaceMountPath"/> without its trailing slash. Use this as
    /// the base when building an absolute container path to a file inside the
    /// workspace — <c>$"{WorkspaceMountPathNoSlash}/{file}"</c> — so the result
    /// is not <c>/spring/workspace//file</c>.
    /// </summary>
    public const string WorkspaceMountPathNoSlash = "/spring/workspace";

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
}
