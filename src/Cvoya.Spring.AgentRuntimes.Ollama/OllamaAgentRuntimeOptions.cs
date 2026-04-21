// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama;

/// <summary>
/// Configuration knobs for the <c>ollama</c> <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime"/>.
/// Bound from the <c>AgentRuntimes:Ollama</c> configuration section by the
/// runtime's DI extension.
/// </summary>
/// <remarks>
/// <para>
/// Ollama exposes both its native <c>/api/*</c> surface and an
/// OpenAI-compatible <c>/v1/chat/completions</c> endpoint on the same port
/// and requires no API key for local installs, so the only operator-facing
/// knob is <see cref="BaseUrl"/>. The runtime carries this value end-to-end
/// — wizard accept-time validation, container-baseline reachability probe,
/// and the eventual unit binding.
/// </para>
/// <para>
/// In a multi-tenant deployment, the per-install configuration JSON
/// (<c>config_json</c>) carries the same <c>BaseUrl</c> field. The host
/// host materialises a per-tenant <c>IOptions&lt;OllamaAgentRuntimeOptions&gt;</c>
/// from the install record so the same runtime instance can serve different
/// tenants pointed at different Ollama endpoints.
/// </para>
/// </remarks>
public class OllamaAgentRuntimeOptions
{
    /// <summary>
    /// The configuration section name: <c>AgentRuntimes:Ollama</c>.
    /// </summary>
    public const string SectionName = "AgentRuntimes:Ollama";

    /// <summary>
    /// Base URL of the Ollama server. For containerised OSS deployments this
    /// is the in-cluster <c>spring-ollama</c> service. macOS operators who run
    /// Ollama on the host (for GPU passthrough) override this to
    /// <c>http://host.containers.internal:11434</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "http://spring-ollama:11434";

    /// <summary>
    /// Timeout (in seconds) applied to the host-side <c>/api/tags</c>
    /// reachability probe invoked by
    /// <see cref="OllamaAgentRuntime.FetchLiveModelsAsync(string, System.Threading.CancellationToken)"/>.
    /// The endpoint is cheap; a slow response usually means the server is
    /// not reachable.
    /// </summary>
    public int HealthCheckTimeoutSeconds { get; set; } = 5;
}