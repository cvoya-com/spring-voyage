// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration for the API host's agent-reachable base URL.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0054 retired the messaging callback surface and its per-turn JWT —
/// <c>sv.messaging.*</c> is served by the single platform MCP server under
/// the MCP session token. <see cref="BaseUrl"/> survives because the
/// OTLP-ingest plane still needs it: <c>DispatcherCallbackEnvironmentBuilder</c>
/// stamps <see cref="Core.Execution.AgentCallbackEnvironmentContract.CallbackUrlEnvVar"/>
/// (<c>SPRING_CALLBACK_URL</c>) onto every runtime container from this value,
/// and <c>LauncherOtelEnvironment</c> derives the <c>/otlp</c> ingest endpoint
/// from it.
/// </para>
/// <para>
/// The value must be the API host's agent-reachable base URL — in the OSS
/// single-host deployment, <c>http://spring-api:8080/</c> on the
/// <c>spring-tenant-default</c> network shared with runtime containers.
/// </para>
/// </remarks>
public class CallbackBaseUrlOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "CallbackBaseUrl";

    /// <summary>
    /// Agent-reachable base URL of the API host. The OTLP-ingest endpoint is
    /// derived from it. When unset, the callback-environment builder throws on
    /// the first launch — surfacing the misconfiguration at launch time.
    /// </summary>
    public string? BaseUrl { get; set; }
}
