// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// A single turn in a tool-use conversation, carrying one or more <see cref="ContentBlock"/>
/// entries authored by either the user or the assistant.
/// </summary>
/// <param name="Role">Either "user" or "assistant".</param>
/// <param name="Content">The ordered content blocks for this turn.</param>
public record ConversationTurn(string Role, IReadOnlyList<ContentBlock> Content);