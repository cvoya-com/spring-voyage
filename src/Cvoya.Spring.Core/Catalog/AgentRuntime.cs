// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

/// <summary>
/// One agent runtime entry from <c>eng/runtime-catalog/runtime-catalog.yaml</c>. Per
/// ADR-0038 decision 2, runtimes are platform configuration; per-runtime
/// behaviour lives in code as
/// <see cref="Cvoya.Spring.Core.Execution.IAgentRuntimeLauncher"/>
/// strategies dispatched by <see cref="Launcher"/>.
/// </summary>
/// <param name="Id">
/// Stable runtime id (e.g. <c>claude-code</c>, <c>codex</c>, <c>gemini</c>,
/// <c>spring-voyage</c>). Persisted on units / agents.
/// </param>
/// <param name="DisplayName">Human-facing label.</param>
/// <param name="DefaultImage">
/// Default container image the wizard pre-fills when an operator selects
/// this runtime.
/// </param>
/// <param name="Launcher">
/// Launcher strategy id (e.g. <c>claude-code-cli</c>, <c>spring-voyage-agent</c>).
/// Resolved via DI to an
/// <see cref="Cvoya.Spring.Core.Execution.IAgentRuntimeLauncher"/>.
/// </param>
/// <param name="ThreadBinding">How the platform delivers the thread id to the runtime.</param>
/// <param name="SystemPromptInjection">How the assembled system prompt reaches the runtime.</param>
/// <param name="ModelProviders">
/// The runtime's allowed providers, each carrying its consumed
/// <see cref="AgentRuntimeProviderEdge.AuthMethod"/> and per-edge
/// <see cref="AgentRuntimeProviderEdge.CredentialEnvVar"/>.
/// </param>
public sealed record AgentRuntime(
    string Id,
    string DisplayName,
    string DefaultImage,
    string Launcher,
    ThreadBinding ThreadBinding,
    SystemPromptInjection SystemPromptInjection,
    IReadOnlyList<AgentRuntimeProviderEdge> ModelProviders)
{
    /// <summary>
    /// Allowed provider ids derived from <see cref="ModelProviders"/> —
    /// the basis for the wizard's "is the provider fixed?" check.
    /// </summary>
    public IReadOnlyList<string> AllowedProviders =>
        ModelProviders.Select(e => e.Id).ToArray();

    /// <summary>
    /// True when the runtime accepts exactly one provider; the wizard hides
    /// the provider picker per ADR-0038 decision 1.
    /// </summary>
    public bool IsProviderFixed => ModelProviders.Count == 1;
}

/// <summary>
/// One <c>(provider, authMethod, credentialEnvVar)</c> edge on an
/// <see cref="AgentRuntime"/>. Per ADR-0038 decision 2, the env-var name
/// depends on both the runtime and the provider, so it is modelled here
/// rather than on the provider.
/// </summary>
/// <param name="Id">Provider id this edge targets.</param>
/// <param name="AuthMethod">
/// The single auth method this runtime consumes from this provider.
/// <c>null</c> when the provider requires no credential (e.g. Ollama).
/// </param>
/// <param name="CredentialEnvVar">
/// Env var the launcher writes the resolved credential into (e.g.
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c>, <c>ANTHROPIC_API_KEY</c>). <c>null</c>
/// when <see cref="AuthMethod"/> is <c>null</c>.
/// </param>
public sealed record AgentRuntimeProviderEdge(
    string Id,
    AuthMethod? AuthMethod = null,
    string? CredentialEnvVar = null);
