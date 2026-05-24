// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Assembles the per-agent system prompt — three sections (platform
/// instructions, unit context, role-specific instructions) — from a
/// <see cref="PromptAssemblyContext"/>.
/// </summary>
/// <remarks>
/// The result is the system prompt the platform delivers to the agent
/// runtime; thread history (prior messages, checkpoints) is NOT part of
/// the assembled prompt — each runtime's session-resume mechanism owns
/// that surface (Claude Code's <c>--resume</c>, the Python SDK's
/// runtime API, equivalents in Codex / Gemini).
/// </remarks>
public interface IPromptAssembler
{
    /// <summary>
    /// Assembles the per-agent system prompt string from the supplied
    /// <paramref name="context"/>. When <paramref name="context"/> is
    /// <c>null</c>, only the platform-instructions section is rendered.
    /// </summary>
    /// <param name="context">
    /// Per-agent inputs (policies, unit + agent skill bundles, agent
    /// instructions, connector prompt fragments). The same instance
    /// is safe to share across concurrent calls since the assembler
    /// only reads it.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The assembled prompt string.</returns>
    Task<string> AssembleAsync(PromptAssemblyContext? context, CancellationToken cancellationToken = default);
}
