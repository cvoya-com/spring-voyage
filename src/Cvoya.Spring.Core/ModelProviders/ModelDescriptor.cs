// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.ModelProviders;

/// <summary>
/// A single model entry in a model provider's catalogue.
/// </summary>
/// <param name="Id">
/// Stable model identifier passed to the backing service (e.g.
/// <c>claude-sonnet-4-6</c>, <c>gpt-4o-mini</c>). Persisted on units — a
/// unit's pinned model id survives catalogue changes.
/// </param>
/// <param name="DisplayName">Human-facing label for UI/CLI surfaces.</param>
/// <param name="ContextWindow">
/// Model context window in tokens, if known. <c>null</c> when the
/// provider cannot report it.
/// </param>
public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    int? ContextWindow);
