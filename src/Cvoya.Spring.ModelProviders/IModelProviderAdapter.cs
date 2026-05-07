// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.ModelProviders;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Catalog;

/// <summary>
/// Adapter strategy for one wire-format family of model providers (per
/// ADR-0038 decision 3). Strategies are registered in DI keyed on the
/// <see cref="AdapterId"/> string, which matches the
/// <see cref="ModelProvider.Adapter"/> field in
/// <c>platform/runtime-catalog.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Adapter ids are the closed set <c>anthropic | openai-compatible | google</c>
/// for v0.1. New OpenAI-compatible providers are config-only edits — no
/// new adapter strategy is required. Genuinely novel wire formats add a
/// new <see cref="IModelProviderAdapter"/> alongside.
/// </para>
/// <para>
/// The adapter contract is intentionally narrow: it owns the wire-format
/// concerns (parsing the live-models response, validating credential
/// format pre-flight) but not the per-tenant credential storage or the
/// dispatch envelope — those stay on host-side services that read the
/// catalogue.
/// </para>
/// </remarks>
public interface IModelProviderAdapter
{
    /// <summary>
    /// Adapter strategy id (e.g. <c>anthropic</c>, <c>openai-compatible</c>,
    /// <c>google</c>). Matches <see cref="ModelProvider.Adapter"/> on the
    /// catalogue entries this adapter serves.
    /// </summary>
    string AdapterId { get; }

    /// <summary>
    /// Best-effort fetch of the provider's live model catalogue. Replaces
    /// the per-runtime <c>FetchLiveModelsAsync</c> on the legacy
    /// <c>IAgentRuntime</c> with a per-provider call so two runtimes
    /// targeting the same provider share the catalogue per ADR-0038
    /// decision 6.
    /// </summary>
    /// <param name="provider">The catalogue entry describing this provider.</param>
    /// <param name="credential">
    /// Raw credential to present to the provider. Empty / null when the
    /// provider's <see cref="ModelProvider.AuthMethods"/> list is empty
    /// (e.g. Ollama).
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<FetchLiveModelsResult> FetchLiveModelsAsync(
        ModelProvider provider,
        string? credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pre-flight check that the supplied credential's <i>format</i> is
    /// plausibly accepted by the provider's API. Intended for the
    /// credential-status probe so the wizard can warn before a real
    /// network round-trip rejects the value.
    /// </summary>
    /// <remarks>
    /// Empty / whitespace credentials must return <c>true</c> — the
    /// "not configured" state is the resolver's concern. A return of
    /// <c>true</c> means the format is plausible; it does not assert
    /// authentication.
    /// </remarks>
    /// <param name="provider">The catalogue entry describing this provider.</param>
    /// <param name="credential">Raw credential to inspect.</param>
    /// <param name="authMethod">
    /// Auth method the caller intends to use (drawn from the catalogue's
    /// per-edge entry). Adapters use this to apply method-specific format
    /// rules — e.g. Anthropic OAuth tokens start with <c>sk-ant-oat</c>,
    /// API keys with <c>sk-ant-api</c>.
    /// </param>
    bool IsCredentialFormatAccepted(
        ModelProvider provider,
        string credential,
        AuthMethod authMethod);
}