// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using System.Text.Json.Serialization;

/// <summary>
/// Request body for <c>POST /v1/containers</c>. Deliberately close to the
/// shape of <c>ContainerConfig</c> in <c>Cvoya.Spring.Core.Execution</c> — the
/// client adapter maps one to the other.
/// </summary>
public record RunContainerRequest
{
    /// <summary>The container image to run.</summary>
    [JsonPropertyName("image")]
    public required string Image { get; init; }

    /// <summary>
    /// Legacy single-string command field. Retained for compatibility with
    /// older worker clients that have not been upgraded to send
    /// <see cref="CommandArgs"/>. When <see cref="CommandArgs"/> is also
    /// set the server prefers the list and ignores this string. When only
    /// this field is set the server splits on whitespace (the same lossy
    /// behaviour the worker had before #1093).
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>
    /// argv-style command vector. Each entry becomes one argv token inside
    /// the container — no shell splitting. Introduced in #1093 to replace
    /// the whitespace-split fragility on the worker side. Preferred over
    /// <see cref="Command"/> when both are sent.
    /// </summary>
    [JsonPropertyName("commandArgs")]
    public IReadOnlyList<string>? CommandArgs { get; init; }

    /// <summary>Environment variables to set in the container.</summary>
    [JsonPropertyName("env")]
    public IDictionary<string, string>? Env { get; init; }

    /// <summary>Volume mount specifications in <c>host:container[:opts]</c> form.</summary>
    [JsonPropertyName("mounts")]
    public IReadOnlyList<string>? Mounts { get; init; }

    /// <summary>Working directory inside the container.</summary>
    [JsonPropertyName("workdir")]
    public string? WorkingDirectory { get; init; }

    /// <summary>Optional timeout in seconds.</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; init; }

    /// <summary>Container network name.</summary>
    [JsonPropertyName("network")]
    public string? NetworkName { get; init; }

    /// <summary>
    /// Additional networks the dispatcher should attach the container to in
    /// addition to <see cref="NetworkName"/>. Emitted as repeated
    /// <c>--network</c> flags. Used by <c>ContainerLifecycleManager</c> to
    /// dual-attach Dapr-fronted containers to a per-tenant bridge on top of
    /// the per-workflow app↔sidecar bridge (ADR 0028 / issue #1166).
    /// </summary>
    [JsonPropertyName("additionalNetworks")]
    public IReadOnlyList<string>? AdditionalNetworks { get; init; }

    /// <summary>Container labels.</summary>
    [JsonPropertyName("labels")]
    public IDictionary<string, string>? Labels { get; init; }

    /// <summary>Additional <c>host:IP</c> entries to inject into /etc/hosts.</summary>
    [JsonPropertyName("extraHosts")]
    public IReadOnlyList<string>? ExtraHosts { get; init; }

    /// <summary>
    /// Optional caller-provided container name. When set, the dispatcher
    /// passes it to <c>podman run --name</c> so the container is reachable
    /// by a stable, predictable hostname on the bridge network. The Dapr
    /// agent lifecycle uses this so the per-launch <c>daprd</c> sidecar
    /// can dial the agent over the bridge via <c>--app-channel-address</c>
    /// — a sidecar in a separate network namespace can't fall back to
    /// <c>127.0.0.1</c> for the app channel.
    /// </summary>
    [JsonPropertyName("containerName")]
    public string? ContainerName { get; init; }

    /// <summary>
    /// When true, run the container in detached mode (equivalent to
    /// <c>IContainerRuntime.StartAsync</c>). When false (default), run to
    /// completion and return the result.
    /// </summary>
    [JsonPropertyName("detached")]
    public bool Detached { get; init; }

    /// <summary>
    /// Optional per-invocation workspace the dispatcher must materialise on
    /// its own host filesystem (under <c>Dispatcher:WorkspaceRoot</c>) and
    /// bind-mount into the container at <see cref="WorkspaceRequest.MountPath"/>.
    /// Synthesised mount is appended to <see cref="Mounts"/>; if
    /// <see cref="WorkingDirectory"/> is null it defaults to the workspace
    /// mount path. The dispatcher deletes the materialised directory when the
    /// run completes (or, for detached starts, when <c>DELETE</c> is called
    /// for the resulting container id). See issue #1042.
    /// </summary>
    [JsonPropertyName("workspace")]
    public WorkspaceRequest? Workspace { get; init; }

    /// <summary>
    /// D3a: optional per-invocation context workspace the dispatcher must
    /// materialise on its own host filesystem and bind-mount into the
    /// container at <see cref="WorkspaceRequest.MountPath"/> (canonical:
    /// <c>/spring/context/</c> per D1 spec § 2.2.2). Carries structured
    /// context files such as <c>agent-definition.yaml</c> and
    /// <c>tenant-config.json</c>. The dispatcher appends the synthesised
    /// mount to <see cref="Mounts"/> and cleans up the materialised directory
    /// on the same lifecycle as <see cref="Workspace"/>. <c>null</c> means
    /// no context mount. See issue #1270.
    /// </summary>
    [JsonPropertyName("contextWorkspace")]
    public WorkspaceRequest? ContextWorkspace { get; init; }
}

/// <summary>
/// Files the dispatcher must materialise into a fresh per-invocation directory
/// before launching a container. Carried by <see cref="RunContainerRequest.Workspace"/>.
/// </summary>
public record WorkspaceRequest
{
    /// <summary>Absolute path inside the container where the dispatcher bind-mounts the workspace.</summary>
    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }

    /// <summary>
    /// File contents keyed by path relative to the workspace root. Paths must
    /// be relative; absolute paths or <c>..</c> traversals are rejected. The
    /// dispatcher creates parent directories as needed and writes each file
    /// with UTF-8 encoding.
    /// </summary>
    [JsonPropertyName("files")]
    public required IDictionary<string, string> Files { get; init; }
}

/// <summary>
/// Response body for <c>POST /v1/containers</c>.
/// </summary>
public record RunContainerResponse
{
    /// <summary>The runtime-assigned container identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Process exit code. Always populated for <c>detached=false</c>; omitted
    /// for detached starts.
    /// </summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>Captured standard output (detached=false only).</summary>
    [JsonPropertyName("stdout")]
    public string? StandardOutput { get; init; }

    /// <summary>Captured standard error (detached=false only).</summary>
    [JsonPropertyName("stderr")]
    public string? StandardError { get; init; }
}

/// <summary>
/// Request body for <c>POST /v1/images/pull</c>. Splits image-pull semantics
/// out from <c>POST /v1/containers</c> because pulls have distinct timeout
/// and failure shapes (slow registry, auth failure, tag-not-found) that
/// <c>UnitValidationWorkflow</c> surfaces differently from a run-time
/// failure. See <c>IContainerRuntime.PullImageAsync</c>.
/// </summary>
public record PullImageRequest
{
    /// <summary>The fully-qualified image reference to pull (e.g. <c>ghcr.io/cvoya/claude:1.2.3</c>).</summary>
    [JsonPropertyName("image")]
    public required string Image { get; init; }

    /// <summary>
    /// Optional pull timeout in seconds. The dispatcher applies this as a
    /// hard wall-clock deadline; if exceeded the response is HTTP 504 and
    /// the client maps it to <see cref="System.TimeoutException"/>.
    /// </summary>
    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// Request body for <c>POST /v1/containers/{id}/probe</c>. Mirrors the
/// narrow primitive on <c>IContainerRuntime.ProbeContainerHttpAsync</c> —
/// the dispatcher executes <c>wget -q --spider</c> inside the named
/// container and returns whether the URL responded 2xx. See the interface
/// docs for the rationale on keeping the surface narrower than a generic
/// <c>exec</c>.
/// </summary>
public record ProbeContainerHttpRequest
{
    /// <summary>The URL to probe; typically a loopback URL inside the container.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

/// <summary>
/// Response body for <c>POST /v1/containers/{id}/probe</c>. The dispatcher
/// collapses every failure mode (DNS failure, non-2xx, missing wget, exited
/// container) into a single boolean so the caller's polling loop owns the
/// retry / timeout decision uniformly.
/// </summary>
public record ProbeContainerHttpResponse
{
    /// <summary>Whether the probed URL answered 2xx.</summary>
    [JsonPropertyName("healthy")]
    public required bool Healthy { get; init; }
}

/// <summary>
/// Request body for <c>POST /v1/probes/transient</c>. Mirrors the
/// <see cref="Cvoya.Spring.Core.Execution.IContainerRuntime.ProbeHttpFromTransientContainerAsync"/>
/// primitive: the dispatcher spawns a throwaway <c>--rm</c> probe container on
/// the named bridge network and asks <paramref name="ProbeContainerHttpRequest.Url"/>.
/// Used for sidecar images that are distroless (no <c>wget</c> / <c>curl</c>
/// in PATH) and therefore cannot be probed via the
/// <c>POST /v1/containers/{id}/probe</c> exec route — the canonical case is
/// the upstream <c>daprio/daprd</c> image.
/// </summary>
public record TransientProbeHttpRequest
{
    /// <summary>Probe container image (e.g. <c>docker.io/curlimages/curl:latest</c>).</summary>
    [JsonPropertyName("probeImage")]
    public required string ProbeImage { get; init; }

    /// <summary>Bridge network the probe container attaches to.</summary>
    [JsonPropertyName("network")]
    public required string Network { get; init; }

    /// <summary>The URL to probe. The host portion is resolved via the network's DNS.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

/// <summary>
/// Response body for <c>POST /v1/probes/transient</c>. Reuses the same
/// boolean-collapse contract as <see cref="ProbeContainerHttpResponse"/>.
/// </summary>
public record TransientProbeHttpResponse
{
    /// <summary>Whether the probed URL answered 2xx.</summary>
    [JsonPropertyName("healthy")]
    public required bool Healthy { get; init; }
}

/// <summary>
/// Request body for <c>POST /v1/containers/{id}/probe-from-host</c>. The
/// dispatcher resolves the container's host-visible IP address, rewrites
/// the URL, and issues a plain HTTP GET from its own process — no
/// <c>podman exec</c>, no in-container tooling required. Used by
/// <see cref="Cvoya.Spring.Core.Execution.IContainerRuntime.ProbeHttpFromHostAsync"/>
/// to probe A2A readiness without depending on what is installed in the
/// workload image (issue #1175).
/// </summary>
public record ProbeFromHostRequest
{
    /// <summary>
    /// The in-container URL to probe (e.g. <c>http://localhost:8999/.well-known/agent.json</c>).
    /// The dispatcher rewrites the host portion to the container's host-routable
    /// IP address before issuing the GET.
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

/// <summary>
/// Response body for <c>POST /v1/containers/{id}/probe-from-host</c>.
/// Uses the same boolean-collapse contract as
/// <see cref="ProbeContainerHttpResponse"/> so the caller's polling loop
/// owns retry and timeout semantics uniformly.
/// </summary>
public record ProbeFromHostResponse
{
    /// <summary>Whether the probed URL answered 2xx.</summary>
    [JsonPropertyName("healthy")]
    public required bool Healthy { get; init; }
}

/// <summary>
/// Request body for <c>POST /v1/containers/{id}/a2a</c> — the dispatcher-
/// proxied A2A message-send primitive that closes the second half of issue
/// #1160. The worker hands the dispatcher the in-container URL it would
/// have called directly, plus the raw JSON body bytes (base64-wrapped on
/// the wire to avoid double-escaping). The dispatcher executes the POST
/// from inside the agent container's network namespace via
/// <c>podman exec -i &lt;id&gt; wget --post-file=/dev/stdin</c> and returns
/// the response body verbatim.
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors <see cref="ProbeContainerHttpRequest"/> on purpose —
/// the two endpoints are the only points where the dispatcher reaches into
/// a container's network namespace, and keeping their wire shapes parallel
/// keeps the security review small. See
/// <c>IContainerRuntime.SendHttpJsonAsync</c> for the rationale on the
/// narrow POST-only / JSON-only contract.
/// </para>
/// <para>
/// Body bytes are carried as base64 (<c>bodyBase64</c>) so the wire stays
/// pure JSON and the worker / dispatcher never have to second-guess content
/// encoding or escape JSON inside JSON. The dispatcher decodes once and
/// streams the original bytes through the wget stdin pipe.
/// </para>
/// </remarks>
public record SendContainerHttpJsonRequest
{
    /// <summary>The in-container URL to POST to (e.g. <c>http://localhost:8999/</c>).</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Base64-encoded UTF-8 JSON request body.</summary>
    [JsonPropertyName("bodyBase64")]
    public required string BodyBase64 { get; init; }
}

/// <summary>
/// Response body for <c>POST /v1/containers/{id}/a2a</c>. Carries the
/// HTTP status the dispatcher observed from the in-container endpoint
/// plus the response body bytes (base64-wrapped). On any failure the
/// status collapses to 502 with an empty body — the worker reconstructs
/// an <see cref="System.Net.Http.HttpResponseMessage"/> from these two
/// fields and the A2A SDK's own retry/timeout policy decides what to do
/// next.
/// </summary>
public record SendContainerHttpJsonResponse
{
    /// <summary>HTTP status code observed from the in-container endpoint.</summary>
    [JsonPropertyName("statusCode")]
    public required int StatusCode { get; init; }

    /// <summary>Base64-encoded response body bytes (empty on failures).</summary>
    [JsonPropertyName("bodyBase64")]
    public required string BodyBase64 { get; init; }
}

/// <summary>
/// Request body for <c>POST /v1/llm/forward</c> and
/// <c>POST /v1/llm/forward/stream</c> — the dispatcher-proxied LLM
/// primitive that closes the hosted-agent half of ADR 0028 Decision E
/// (issue #1168). The worker hands the dispatcher the upstream URL it
/// would have called directly (e.g.
/// <c>http://tenant-ollama:11434/v1/chat/completions</c>) plus the raw
/// request body bytes (base64-wrapped on the wire to avoid double-
/// escaping). The dispatcher executes the POST from its own process and
/// returns the upstream status / body verbatim.
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors <see cref="SendContainerHttpJsonRequest"/> on
/// purpose — both endpoints are dispatcher-proxied HTTP POSTs and
/// keeping the wire shapes parallel keeps the security review small.
/// The differences are the headers field (LLM proxies need to forward
/// <c>x-api-key</c> / <c>anthropic-version</c> on managed-provider
/// requests, A2A does not) and the streaming sibling endpoint.
/// </para>
/// <para>
/// Body bytes are carried as base64 (<c>bodyBase64</c>) so the wire
/// stays pure JSON and neither side has to second-guess content
/// encoding. An empty <c>bodyBase64</c> is allowed; the dispatcher
/// sends an empty request body in that case.
/// </para>
/// </remarks>
public record LlmForwardRequest
{
    /// <summary>Upstream URL to POST to (e.g. <c>http://tenant-ollama:11434/v1/chat/completions</c>).</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Base64-encoded UTF-8 request body bytes; empty string when there is no body.</summary>
    [JsonPropertyName("bodyBase64")]
    public required string BodyBase64 { get; init; }

    /// <summary>
    /// Optional request headers to forward verbatim to the upstream
    /// (typically <c>x-api-key</c>, <c>anthropic-version</c> for
    /// managed providers). <c>Content-Type</c> defaults to
    /// <c>application/json</c> when the body is non-empty and no
    /// override is supplied.
    /// </summary>
    [JsonPropertyName("headers")]
    public IDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// Response body for <c>POST /v1/llm/forward</c>. Carries the HTTP
/// status the dispatcher observed from the upstream LLM plus the
/// response body bytes (base64-wrapped). On any transport failure the
/// dispatcher returns HTTP 502 at the envelope level (the worker
/// further collapses that to <c>StatusCode = 502, Body = []</c>).
/// </summary>
public record LlmForwardResponse
{
    /// <summary>HTTP status code observed from the upstream LLM.</summary>
    [JsonPropertyName("statusCode")]
    public required int StatusCode { get; init; }

    /// <summary>Base64-encoded response body bytes (empty when the upstream returned no body).</summary>
    [JsonPropertyName("bodyBase64")]
    public required string BodyBase64 { get; init; }
}

/// <summary>
/// Request body for <c>POST /v1/networks</c>. The dispatcher creates the
/// network idempotently — repeating the call with the same name is a 200,
/// not a 409, so callers (notably <c>ContainerLifecycleManager</c>) can
/// re-issue the create without first inspecting existence.
/// </summary>
public record CreateNetworkRequest
{
    /// <summary>The container network name to create.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// Problem shape emitted by the dispatcher for error responses.
/// </summary>
public record DispatcherErrorResponse
{
    /// <summary>Short machine-readable error code.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}