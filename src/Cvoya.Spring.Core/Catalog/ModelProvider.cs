// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

/// <summary>
/// A model provider — the company / endpoint hosting a set of LLMs. Per
/// ADR-0038 decision 3, providers are platform configuration loaded from
/// <c>platform/runtime-catalog.yaml</c>; there are no per-provider classes.
/// Wire-format differences are handled by a small set of
/// <c>IModelProviderAdapter</c> strategies registered by <see cref="Adapter"/> id.
/// </summary>
/// <param name="Id">
/// Stable provider id (e.g. <c>anthropic</c>, <c>openai</c>, <c>google</c>,
/// <c>ollama</c>). Persisted on credentials, model references, and tenant
/// installs.
/// </param>
/// <param name="DisplayName">Human-facing label for UI / CLI surfaces.</param>
/// <param name="ApiBaseUrl">Base URL of the provider's HTTP API.</param>
/// <param name="ModelsEndpoint">
/// Path appended to <see cref="ApiBaseUrl"/> for live-model enumeration
/// (e.g. <c>/v1/models</c>, <c>/api/tags</c>). Consumed by the adapter's
/// <c>FetchLiveModelsAsync</c>.
/// </param>
/// <param name="Adapter">
/// Adapter strategy id (e.g. <c>anthropic</c>, <c>openai-compatible</c>,
/// <c>google</c>). Names the wire-format family, not the company. The DI
/// container resolves an <c>IModelProviderAdapter</c> keyed on this value.
/// </param>
/// <param name="AuthMethods">
/// Auth methods the provider's API will accept. Empty list (<c>[]</c>)
/// denotes a provider that requires no credential — e.g. Ollama's local
/// endpoint in v0.1.
/// </param>
/// <param name="LlmApiContract">
/// LLM API surface this provider implements (<c>{name, version}</c>).
/// Maps to a Dapr Conversation component by convention
/// (<c>llm-{provider.id}.yaml</c>).
/// </param>
/// <param name="DefaultModels">
/// Cold-start seed list of model ids the platform ships for the provider.
/// Replaced once live-fetch is wired; preserved as offline fallback.
/// </param>
public sealed record ModelProvider(
    string Id,
    string DisplayName,
    string ApiBaseUrl,
    string ModelsEndpoint,
    string Adapter,
    IReadOnlyList<AuthMethod> AuthMethods,
    LlmApiContract LlmApiContract,
    IReadOnlyList<string> DefaultModels);
