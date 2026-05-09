// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Platform-side endpoint configuration for the <c>IAgentContext</c> bootstrap
/// bundle (D1 spec § 2 — D3a Stage 3 of ADR-0029).
/// </summary>
/// <remarks>
/// <para>
/// Binds from the <c>AgentContext</c> configuration section. Each field maps
/// to one or more canonical env vars the platform delivers to agent containers
/// per the D1 spec § 2.2.1.
/// </para>
/// <para>
/// LLM credentials are resolved at launch time via <see cref="ILlmCredentialResolver"/>
/// and are not stored in options — they are per-agent-scoped and change per
/// provider/unit/tenant combination. The <see cref="LlmProviderUrl"/> here is
/// the platform-level endpoint URL (e.g. the Ollama or Anthropic proxy base
/// URL); the per-launch credential is resolved separately and injected as
/// <c>SPRING_LLM_PROVIDER_TOKEN</c>.
/// </para>
/// </remarks>
public class AgentContextOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "AgentContext";

    /// <summary>
    /// The public Web API base URL the platform delivers as
    /// <c>SPRING_BUCKET2_URL</c>. Agent containers call this endpoint to
    /// send A2A messages back into the platform (Bucket 2, D1 spec § 4).
    /// Example: <c>https://api.example.com/api/v1/</c>.
    /// </summary>
    public string? Bucket2Url { get; set; }

    /// <summary>
    /// The LLM provider endpoint URL delivered as
    /// <c>SPRING_LLM_PROVIDER_URL</c>. The container uses the provider's
    /// native API at this base URL (e.g. Ollama REST, OpenAI-compatible).
    /// When unset the builder falls back to the Ollama <c>BaseUrl</c> from
    /// <see cref="OllamaOptions"/> (the default OSS deployment topology).
    /// </summary>
    public string? LlmProviderUrl { get; set; }

    /// <summary>
    /// The OpenTelemetry collector endpoint URL delivered as
    /// <c>SPRING_TELEMETRY_URL</c>. Containers emit traces / metrics / logs
    /// to this address using the OTLP exporter. When unset the builder omits
    /// <c>SPRING_TELEMETRY_URL</c> from the bootstrap (the SDK then uses its
    /// own default or disables telemetry).
    /// </summary>
    public string? TelemetryUrl { get; set; }

    /// <summary>
    /// Optional static bearer token for the telemetry collector. Delivered as
    /// <c>SPRING_TELEMETRY_TOKEN</c> when non-null. Most OSS deployments run
    /// an unauthenticated collector on the tenant network — leave null.
    /// </summary>
    public string? TelemetryToken { get; set; }
}
