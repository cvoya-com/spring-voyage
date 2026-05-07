// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

/// <summary>
/// A specific large language model identified by its
/// (<paramref name="Provider"/>, <paramref name="Id"/>) pair. Per ADR-0038
/// decision 1, the provider is intrinsic to the model — the user-facing
/// execution config carries the model object, never a separate
/// <c>provider</c> slot.
/// </summary>
/// <param name="Provider">
/// Stable id of the <see cref="ModelProvider"/> that hosts this model
/// (e.g. <c>anthropic</c>, <c>openai</c>, <c>google</c>, <c>ollama</c>).
/// References a <see cref="ModelProvider.Id"/> in the runtime catalogue.
/// </param>
/// <param name="Id">
/// Stable, provider-scoped model id (e.g. <c>claude-opus-4-7</c>,
/// <c>gpt-4o-mini</c>, <c>llama3.2:3b</c>). Persisted on units / agents —
/// a unit's pinned model id survives provider catalogue changes.
/// </param>
public sealed record Model(string Provider, string Id);