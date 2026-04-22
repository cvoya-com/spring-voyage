// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration for the Dapr sidecar containers that
/// <see cref="DaprSidecarManager"/> launches alongside agent containers.
/// Bound from the <c>Dapr:Sidecar</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2 of #522 lifted these knobs out of a hard-coded constant + the
/// shared <c>ContainerRuntime</c> options block. The image tag was
/// previously baked into <see cref="DaprSidecarManager"/>; the health
/// timeout was inlined inside <c>WaitForHealthyAsync</c>. Both are now
/// configurable so operators running a pinned daprd version (e.g. matching
/// a Dapr control-plane release on their cluster) can override without
/// recompiling, and slow-CPU dev environments can extend the health window
/// past 30s without forking the manager.
/// </para>
/// </remarks>
public class DaprSidecarOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dapr:Sidecar";

    /// <summary>
    /// Container image used to launch Dapr sidecars. Defaults to the
    /// floating <c>latest</c> tag of the official daprd image — operators
    /// should pin to a specific Dapr minor version (e.g.
    /// <c>daprio/daprd:1.14.4</c>) for production deployments to keep
    /// sidecar / control-plane / SDK versions aligned.
    /// </summary>
    public string Image { get; set; } = "daprio/daprd:latest";

    /// <summary>
    /// Maximum time <c>WaitForHealthyAsync</c> polls
    /// <c>http://&lt;sidecar&gt;:&lt;httpPort&gt;/v1.0/healthz</c> before
    /// returning false. Defaults to 30 seconds. Slow-CPU dev environments
    /// (CI runners under heavy parallelism, ARM SBCs) may need longer; the
    /// daprd boot path itself is bounded by the components folder size.
    /// </summary>
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Polling interval used while waiting for sidecar health. Kept short
    /// so the typical sub-second daprd boot doesn't pay the full
    /// <see cref="HealthTimeout"/> on first probe.
    /// </summary>
    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Optional path to the Dapr components directory the sidecar bind-mounts
    /// at <c>/components</c>. Moved here from <c>ContainerRuntimeOptions</c>
    /// in Stage 2 of #522 because it's a sidecar-shape concern, not a
    /// container-runtime-default concern. Worker hosts that previously bound
    /// <c>ContainerRuntime:DaprComponentsPath</c> should now bind
    /// <c>Dapr:Sidecar:ComponentsPath</c>; the same file path works in both
    /// keys (only the section moved). When unset the sidecar boots with no
    /// custom components folder, which is the dev-default.
    /// </summary>
    public string? ComponentsPath { get; set; }
}