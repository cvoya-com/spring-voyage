// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// A single model entry in an <see cref="IAgentRuntime"/>'s catalog.
/// </summary>
/// <param name="Id">
/// Stable model identifier passed to the backing service (e.g.
/// <c>claude-sonnet-4-6</c>, <c>gpt-4o-mini</c>). Persisted on units — a
/// unit's pinned model id survives catalog changes.
/// </param>
/// <param name="DisplayName">Human-facing label for UI/CLI surfaces.</param>
/// <param name="ContextWindow">
/// Model context window in tokens, if known. <c>null</c> when the runtime
/// cannot report it.
/// </param>
public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    int? ContextWindow);