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
    public const string WorkspaceMountPath = "/spring/workspace/";

    /// <summary>Env var name the D1 spec mandates for the workspace mount path.</summary>
    public const string WorkspacePathEnvVar = "SPRING_WORKSPACE_PATH";
}
