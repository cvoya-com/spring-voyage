// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration for the agent-facing messaging callback surface
/// (<c>sv.messaging.send</c> / <c>sv.messaging.broadcast</c> + the
/// messaging MCP server).
/// </summary>
/// <remarks>
/// <para>
/// The orchestration callback endpoints relocated from the dispatcher onto
/// the Dapr-connected API host (#2586): the dispatcher runs as a bare host
/// process with no Dapr sidecar and cannot invoke the recipient actor.
/// The launcher stamps <see cref="Core.Execution.AgentCallbackEnvironmentContract.CallbackUrlEnvVar"/>
/// (<c>SPRING_CALLBACK_URL</c>) onto every runtime container from
/// <see cref="BaseUrl"/>; <c>LauncherCallbackEnvironment</c> builds the
/// <c>spring-orchestration</c> MCP URL off it, and <c>LauncherOtelEnvironment</c>
/// derives the sibling <c>/otlp</c> ingest endpoint from the same base.
/// </para>
/// <para>
/// The value must be the API host's agent-reachable base URL — in the OSS
/// single-host deployment, <c>http://spring-api:8080/</c> on the
/// <c>spring-tenant-default</c> network shared with runtime containers.
/// </para>
/// </remarks>
public class OrchestrationCallbackOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OrchestrationCallback";

    /// <summary>
    /// Agent-reachable base URL of the API host that serves the
    /// orchestration callback endpoints. When unset, the callback-environment
    /// builder throws on the first launch — surfacing the misconfiguration
    /// at launch time rather than letting an agent come up with a
    /// <c>sv.messaging.send</c> tool that points nowhere.
    /// </summary>
    public string? BaseUrl { get; set; }
}
