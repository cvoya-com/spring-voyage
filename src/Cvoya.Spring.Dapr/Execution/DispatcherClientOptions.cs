// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration options for <see cref="DispatcherClientContainerRuntime"/>.
/// Bound from the <c>Dispatcher</c> configuration section in the worker host.
/// </summary>
public class DispatcherClientOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dispatcher";

    /// <summary>
    /// Base URL of the <c>spring-dispatcher</c> service. The dispatcher
    /// runs as a host process (issue #1063) so workers reach it via the
    /// container-runtime's host-loopback DNS name —
    /// <c>http://host.containers.internal:8090/</c> on Podman or
    /// <c>http://host.docker.internal:8090/</c> on Docker. When unset,
    /// the client throws on the first call — which surfaces the
    /// misconfiguration at dispatch time rather than silently falling
    /// back to an in-process runtime.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Bearer token the worker presents to the dispatcher on every request.
    /// Issued at deploy time and scoped to a tenant on the dispatcher side.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Optional override for the worker-side <see cref="HttpClient.Timeout"/>
    /// applied to every dispatcher request. When <see langword="null"/>
    /// (the default), the client uses
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>: synchronous
    /// container runs (<c>POST /v1/containers</c>) can legitimately take
    /// minutes for a Claude Code or Codex agent turn, and the dispatcher
    /// already owns the per-run deadline via
    /// <c>ContainerConfig.Timeout</c> / <c>TimeoutSeconds</c> on the wire.
    /// A worker-side cap shorter than that is a footgun — when the
    /// HttpClient default of 100 s fires first the worker drops the
    /// connection, the dispatcher sees a client abort and kills the
    /// container, and the user never receives a response (see issue #1063
    /// / #522 follow-up: the original Stage 2 cutover hit exactly this).
    /// Operators can still pin a hard ceiling (for example, in a
    /// multi-tenant deployment that wants a sane upper bound) by setting
    /// <c>Dispatcher:RequestTimeout</c> to a duration like
    /// <c>"00:30:00"</c>.
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; }
}