// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

/// <summary>
/// Identifies the operational role a host plays in a Spring Voyage
/// deployment. The role selects which subset of the shared Dapr DI graph a
/// host actually activates — most importantly, which execution-side
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> registrations
/// start when the host boots.
/// </summary>
/// <remarks>
/// <para>
/// Both Spring Voyage hosts call the same DI entry point
/// (<c>AddCvoyaSpringDapr</c>) and therefore compose the same service graph.
/// Before host roles existed, every execution hosted service started in both
/// hosts: the API host needlessly bound the in-process MCP port, provisioned
/// agent volumes, and polled container health even though only the Worker
/// drives delegated execution. The role threads a single explicit signal
/// through the DI entry point so the execution layer can gate those hosted
/// services to the host that actually owns them (ADR-0052).
/// </para>
/// <para>
/// The role only gates <em>hosted-service</em> registrations. The underlying
/// DI singletons remain registered unconditionally on both hosts so API-side
/// code that resolves them as plain singletons (for example, the agent
/// endpoints reading <c>PersistentAgentRegistry</c>) keeps working regardless
/// of the host role.
/// </para>
/// </remarks>
public enum SpringHostRole
{
    /// <summary>
    /// The HTTP front door (<c>spring-api</c>). Serves the tenant and
    /// platform REST surface, the OTLP ingest plane, and the build-time
    /// OpenAPI document. It does not own delegated-execution supervision, so
    /// the worker-only execution hosted services
    /// (<c>AgentVolumeManager</c>, <c>PersistentAgentRegistry</c>,
    /// <c>EphemeralAgentRegistry</c>, <c>ContainerHealthMetricsService</c>)
    /// do not start under this role. This is the default so the ~20 existing
    /// single-argument <c>AddCvoyaSpringDapr</c> call sites — predominantly
    /// test harnesses — keep compiling and behaving as a non-execution host.
    /// </summary>
    HttpFrontDoor = 0,

    /// <summary>
    /// The execution host (<c>spring-worker</c>). Owns the Dapr actors, EF
    /// Core migrations, the default-tenant bootstrap, and delegated-execution
    /// supervision. The execution-side hosted services
    /// (<c>AgentVolumeManager</c>, <c>PersistentAgentRegistry</c>,
    /// <c>EphemeralAgentRegistry</c>, <c>ContainerHealthMetricsService</c>)
    /// start only under this role.
    /// </summary>
    ExecutionHost = 1,
}
