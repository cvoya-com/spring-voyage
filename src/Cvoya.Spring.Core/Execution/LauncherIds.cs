// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Closed set of launcher strategy ids referenced by
/// <c>platform/runtime-catalog.yaml</c>'s <c>launcher</c> field
/// (ADR-0038 decision 2). Each id resolves to one
/// <see cref="IAgentRuntimeLauncher"/> implementation registered in DI.
/// </summary>
/// <remarks>
/// Lives in <c>Cvoya.Spring.Core</c> so callers in <c>Cvoya.Spring.Dapr</c>
/// (the dispatch coordinator and lifecycle services) can refer to the
/// canonical id without depending on <c>Cvoya.Spring.AgentRuntimes</c>.
/// </remarks>
public static class LauncherIds
{
    /// <summary>Strategy id for the Claude Code CLI launcher.</summary>
    public const string ClaudeCodeCli = "claude-code-cli";

    /// <summary>Strategy id for the Codex CLI launcher.</summary>
    public const string CodexCli = "codex-cli";

    /// <summary>Strategy id for the Gemini CLI launcher.</summary>
    public const string GeminiCli = "gemini-cli";

    /// <summary>Strategy id for the Spring Voyage Agent (Python A2A) launcher.</summary>
    public const string SpringVoyageAgent = "spring-voyage-agent";
}