// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Closed set of launcher strategy ids referenced by
/// <c>eng/runtime-catalog/runtime-catalog.yaml</c>'s <c>launcher</c> field
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

    /// <summary>
    /// Strategy id for the generic A2A-process launcher (ADR-0066) — a
    /// long-running, always-on container that hosts an external
    /// orchestration engine (e.g. LangGraph) and speaks A2A natively. Unlike
    /// <see cref="SpringVoyageAgent"/> it is image-agnostic: it stamps the
    /// platform env contract and the system-prompt bundle, then defers to the
    /// image's own ENTRYPOINT. No per-engine launcher is needed.
    /// </summary>
    public const string A2AProcess = "a2a-process";
}
