// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Single source of truth for translating an <see cref="AgentLaunchSpec"/>
/// into a <see cref="ContainerConfig"/>. Used by every dispatch path that
/// runs an agent container — ephemeral dispatch, persistent auto-start, and
/// the explicit persistent deploy lifecycle — so the three sites cannot
/// drift in how they forward spec fields (env vars, volume mounts, working
/// directory, workspace, argv).
/// </summary>
/// <remarks>
/// <para>
/// PR 2 of the #1087 series. PR 1 introduced the new
/// <see cref="AgentLaunchSpec"/> fields (most notably <c>Argv</c>) and PR 2
/// wires the builder up; PR 4 will populate <c>Argv</c> in the launchers so
/// the dispatcher actually invokes the agent tool. Today every launcher
/// returns <c>Argv == null</c>, so <see cref="ContainerConfig.Command"/>
/// stays <c>null</c> and the container falls back to the image's default
/// ENTRYPOINT/CMD — preserving the no-op semantics until PR 4.
/// </para>
/// <para>
/// The builder always sets <c>host.docker.internal:host-gateway</c> as an
/// extra host so containers running on Linux can reach the dispatcher /
/// MCP server on the host. Callers can append additional hosts via
/// <paramref name="extraHosts"/>; those entries follow the baseline.
/// </para>
/// <para>
/// Environment variables from <paramref name="extraEnv"/> merge with
/// <see cref="AgentLaunchSpec.EnvironmentVariables"/>. On a key collision
/// the value from <paramref name="extraEnv"/> wins, because callers pass
/// extras to override or augment what the launcher produced (e.g. a
/// per-deployment secret) and surprising the caller by silently keeping the
/// launcher's value would be worse than overwriting it.
/// </para>
/// </remarks>
public static class ContainerConfigBuilder
{
    /// <summary>
    /// Default extra-hosts entry that every agent container needs so it can
    /// reach the dispatcher / MCP server running on the host on Linux. See
    /// <see cref="ContainerConfig.ExtraHosts"/>.
    /// </summary>
    private const string HostGatewayEntry = "host.docker.internal:host-gateway";

    /// <summary>
    /// Translates a launcher's <see cref="AgentLaunchSpec"/> into a
    /// <see cref="ContainerConfig"/>.
    /// </summary>
    /// <param name="image">
    /// Fully-qualified container image reference. The dispatcher resolves
    /// this from the agent definition (or persistent-deploy override) before
    /// calling the builder; the builder does not consult the spec for it.
    /// </param>
    /// <param name="spec">The launcher-produced launch specification.</param>
    /// <param name="extraEnv">
    /// Additional environment variables to merge on top of
    /// <see cref="AgentLaunchSpec.EnvironmentVariables"/>. On a key collision
    /// the entry from <paramref name="extraEnv"/> wins. <c>null</c> is
    /// equivalent to passing an empty sequence.
    /// </param>
    /// <param name="extraHosts">
    /// Additional <c>host:IP</c> entries to append after the baseline
    /// <c>host.docker.internal:host-gateway</c>. <c>null</c> is equivalent
    /// to passing an empty sequence.
    /// </param>
    /// <returns>The container configuration ready to hand to <see cref="IContainerRuntime"/>.</returns>
    public static ContainerConfig Build(
        string image,
        AgentLaunchSpec spec,
        IEnumerable<KeyValuePair<string, string>>? extraEnv = null,
        IEnumerable<string>? extraHosts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(spec);

        return new ContainerConfig(
            Image: image,
            Command: spec.Argv is { Count: > 0 } ? spec.Argv : null,
            EnvironmentVariables: MergeEnvironment(spec.EnvironmentVariables, extraEnv),
            VolumeMounts: spec.ExtraVolumeMounts,
            ExtraHosts: BuildExtraHosts(extraHosts),
            // The fallback to WorkspaceMountPath only fires when the launcher
            // actually populated a workspace (i.e. WorkspaceFiles is non-empty).
            // Launchers like ClaudeCodeLauncher write CLAUDE.md / .mcp.json into
            // the workspace and run their tool from cwd, so they need the workdir
            // override; launchers like DaprAgentLauncher carry an empty workspace
            // (their prompt arrives via env vars) and ship images whose CMD is
            // relative to a fixed image workdir (e.g. /app for python agent.py).
            // Overriding their workdir to /workspace would break the relative
            // CMD lookup and the container would exit immediately. See #1159.
            WorkingDirectory: spec.WorkingDirectory
                ?? (spec.WorkspaceFiles.Count > 0 ? spec.WorkspaceMountPath : null),
            Workspace: new ContainerWorkspace(
                MountPath: spec.WorkspaceMountPath,
                Files: spec.WorkspaceFiles));
    }

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> baseEnv,
        IEnumerable<KeyValuePair<string, string>>? extraEnv)
    {
        if (extraEnv is null)
        {
            return baseEnv;
        }

        var merged = new Dictionary<string, string>(baseEnv, StringComparer.Ordinal);
        foreach (var kvp in extraEnv)
        {
            merged[kvp.Key] = kvp.Value;
        }

        return merged;
    }

    private static IReadOnlyList<string> BuildExtraHosts(IEnumerable<string>? extraHosts)
    {
        if (extraHosts is null)
        {
            return [HostGatewayEntry];
        }

        var hosts = new List<string> { HostGatewayEntry };
        hosts.AddRange(extraHosts);
        return hosts;
    }
}