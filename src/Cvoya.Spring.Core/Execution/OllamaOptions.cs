// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Configuration options for the Ollama <see cref="IAiProvider"/>
/// implementation. Binds from the <c>LanguageModel:Ollama</c> section.
/// </summary>
/// <remarks>
/// <para>
/// Ollama exposes both its native API (<c>/api/generate</c>, <c>/api/chat</c>) and an
/// OpenAI-compatible surface (<c>/v1/chat/completions</c>) on the same port. The provider
/// uses the OpenAI-compatible endpoint for parity with the rest of the LLM ecosystem and
/// to keep the payload shape interchangeable with hosted OpenAI-compatible providers. No
/// API key is required.
/// </para>
/// <para>
/// For OSS single-host deployments the default <see cref="BaseUrl"/> points at the
/// <c>spring-ollama</c> container on <c>spring-net</c>. Operators on macOS where container
/// GPU passthrough is not available should run Ollama on the host and override
/// <see cref="BaseUrl"/> to <c>http://host.containers.internal:11434</c> — see the
/// developer docs at <c>docs/developer/local-ai-ollama.md</c>.
/// </para>
/// <para>
/// The multi-tenant cloud host binds a different <see cref="OllamaOptions"/> instance per
/// tenant by pre-registering its own <c>IOptions&lt;OllamaOptions&gt;</c> before calling
/// <c>AddCvoyaSpringOllamaLlm</c>. Since OSS registrations use <c>TryAdd*</c>, the cloud's
/// resolver wins without any changes on this side.
/// </para>
/// <para>
/// Lives in <c>Cvoya.Spring.Core</c> (rather than <c>Cvoya.Spring.Dapr</c>) so the
/// launcher project (<c>Cvoya.Spring.AgentRuntimes</c>) can bind it directly without
/// taking a dependency on Dapr (ADR-0038 Chunk 2a).
/// </para>
/// </remarks>
public class OllamaOptions
{
    /// <summary>
    /// The configuration section name: <c>LanguageModel:Ollama</c>.
    /// </summary>
    public const string SectionName = "LanguageModel:Ollama";

    /// <summary>
    /// When <c>true</c>, the Ollama provider is registered as the primary
    /// <see cref="IAiProvider"/>. When <c>false</c> (the default), the existing
    /// provider (e.g. Anthropic) remains active.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base URL of the Ollama server. For containerised deployments this is the
    /// <c>spring-ollama</c> service on <c>spring-net</c>. For host-installed Ollama on
    /// macOS set to <c>http://host.containers.internal:11434</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "http://spring-ollama:11434";

    /// <summary>
    /// Default model to use for completions. Must be present in the target Ollama
    /// server — the deploy script will attempt to pull this model on first run.
    /// <c>llama3.2:3b</c> is a good balance of capability and resource footprint for
    /// dev workloads.
    /// </summary>
    public string DefaultModel { get; set; } = "llama3.2:3b";

    /// <summary>
    /// Maximum tokens the provider asks Ollama to generate per completion.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Timeout (in seconds) for the health-check probe against <c>/api/tags</c>.
    /// Kept short because the endpoint is cheap and a slow response usually means
    /// the server is not reachable.
    /// </summary>
    public int HealthCheckTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// When <c>true</c>, an unhealthy Ollama server at startup aborts the host. When
    /// <c>false</c> (the default for dev) the host logs a warning and continues —
    /// calls to the provider will fail until Ollama is reachable. Production
    /// deployments that rely on Ollama should set this to <c>true</c>.
    /// </summary>
    public bool RequireHealthyAtStartup { get; set; }
}