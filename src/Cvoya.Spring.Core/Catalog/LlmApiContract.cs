// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

/// <summary>
/// Names the LLM API surface a <see cref="ModelProvider"/> implements as a
/// structured <c>{name, version}</c> value. Per ADR-0038 decision 3:
/// </summary>
/// <param name="Name">
/// Closed enum on contract family: <c>anthropic | openai | google</c> for
/// v0.1. Two providers may share a contract name when their wire formats
/// match (e.g. Ollama exposes an OpenAI-compatible chat-completions
/// surface).
/// </param>
/// <param name="Version">
/// Contract version (currently <c>v1</c> for every contract). Carried
/// explicitly so a future Anthropic Messages v2 / OpenAI v2 etc. can be
/// added without rewriting the schema.
/// </param>
public sealed record LlmApiContract(string Name, string Version);