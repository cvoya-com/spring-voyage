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
    /// Container image used to launch Dapr sidecars. Defaults to a pinned
    /// minor (currently <c>daprio/daprd:1.17.4</c>) — the floating
    /// <c>:latest</c> tag has resolved to <c>1.15.14</c> for a while, and
    /// dapr-agents' Alpha2 Conversation client refuses to talk to
    /// anything older than <c>1.16.0</c> (it surfaces as
    /// <c>!!!!! Dapr Runtime Version 1.15.14 is not supported with Alpha2
    /// Dapr Chat Client</c> in the agent log when the loop's first LLM
    /// turn lands). The pinned value must stay aligned with
    /// <c>deployment/spring.env.example</c>'s <c>DAPR_IMAGE</c> (the
    /// static placement / scheduler / per-app sidecars) and with the
    /// Dapr SDK version pinned in <c>Directory.Packages.props</c> so the
    /// control plane, SDK, and per-launch sidecars all speak the same
    /// minor.
    /// </summary>
    public string Image { get; set; } = "daprio/daprd:1.17.4";

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

    /// <summary>
    /// Dapr placement service <c>host:port</c> for workflow / actor runtimes. When
    /// set, <see cref="DaprSidecarManager"/> appends
    /// <c>--placement-host-address</c> to daprd. Defaults to
    /// <c>spring-placement:50005</c> in OSS so delegated Dapr agent containers
    /// can run <c>dapr-agents</c> workflow loops once the sidecar is dual-
    /// attached to the same tenant bridge as the control plane (see ADR 0028
    /// "V2 interim" deployment).
    /// </summary>
    public string? PlacementHostAddress { get; set; } = "spring-placement:50005";

    /// <summary>
    /// Dapr scheduler <c>host:port</c>. When set, <see cref="DaprSidecarManager"/>
    /// appends <c>--scheduler-host-address</c> to daprd. Defaults to
    /// <c>spring-scheduler:50006</c>.
    /// </summary>
    public string? SchedulerHostAddress { get; set; } = "spring-scheduler:50006";

    /// <summary>
    /// Host path to the <c>delegated-dapr-agent</c> component profile (Conversation
    /// + Redis for workflows) bind-mounted into <c>daprd</c>. Must be a path on
    /// the <b>machine that runs the dispatcher</b> (e.g.
    /// <c>${REPO_ROOT}/dapr/components/delegated-dapr-agent</c>); a literal
    /// <c>/dapr/...</c> only exists inside some container images, not on macOS/Linux
    /// hosts, and would make Podman fail with <c>statfs ... no such file</c>. The
    /// worker reads the value from <c>Dapr:Sidecar:DelegatedDaprAgentComponentsPath</c>
    /// (typically via <c>deployment/spring.env</c>) and forwards it to the dispatcher
    /// as a bind-mount source. When unset, <see cref="ContainerLifecycleManager"/>
    /// falls back to <see cref="ComponentsPath"/> (may be insufficient for
    /// <c>dapr-agent</c>).
    /// </summary>
    public string? DelegatedDaprAgentComponentsPath { get; set; }

    /// <summary>
    /// Optional daprd global config file, bind-mounted in the sidecar. OSS leaves
    /// this empty so daprd uses defaults; platform sidecars in
    /// <c>deployment/deploy.sh</c> use <c>/config/config.yaml</c>.
    /// </summary>
    public string? DaprConfigFilePath { get; set; }

    /// <summary>
    /// Container image used as a throwaway probe container by
    /// <see cref="DaprSidecarManager.WaitForHealthyAsync"/>. The upstream
    /// <c>daprio/daprd</c> image is effectively distroless (no <c>wget</c>,
    /// no <c>curl</c>) so probes must run from a sibling container on the
    /// same bridge network — this is exactly what
    /// <c>deployment/deploy.sh</c>'s <c>wait_sidecar_ready</c> already
    /// does. Defaults to <c>docker.io/curlimages/curl:latest</c>; air-gapped
    /// deployments should override to a mirrored tag.
    /// </summary>
    public string CurlProbeImage { get; set; } = "docker.io/curlimages/curl:latest";
}