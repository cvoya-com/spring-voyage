// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Bundles the artefacts produced when assembling a prompt for a tool-use capable
/// <see cref="IAiProvider"/>: the system prompt text, the tool definitions to expose,
/// and the initial conversation turns to seed the multi-turn loop.
/// </summary>
/// <param name="SystemPrompt">The system prompt string.</param>
/// <param name="Tools">The tools the model is permitted to call.</param>
/// <param name="InitialTurns">The initial conversation turns (typically one user turn).</param>
public record PromptAssemblyResult(
    string SystemPrompt,
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<ConversationTurn> InitialTurns);