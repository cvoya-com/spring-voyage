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

    /// <summary>Optional command to execute inside the container.</summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

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