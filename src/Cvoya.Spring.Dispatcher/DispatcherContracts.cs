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

    /// <summary>Container labels.</summary>
    [JsonPropertyName("labels")]
    public IDictionary<string, string>? Labels { get; init; }

    /// <summary>Additional <c>host:IP</c> entries to inject into /etc/hosts.</summary>
    [JsonPropertyName("extraHosts")]
    public IReadOnlyList<string>? ExtraHosts { get; init; }

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