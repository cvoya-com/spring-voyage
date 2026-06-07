// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntimeLauncher"/> for OpenAI Codex containers. Describes a
/// per-invocation workspace containing:
/// <list type="bullet">
///   <item><c>AGENTS.md</c> — the assembled system prompt (all four layers).
///         Codex reads this file as its instructions equivalent of Claude Code's <c>CLAUDE.md</c>.</item>
///   <item><c>.mcp.json</c> — MCP server endpoint + bearer token the Codex agent will dial.</item>
/// </list>
/// These files are written into the per-agent persistent workspace volume —
/// the single workspace mount at <see cref="AgentWorkspaceContract.WorkspaceMountPath"/>
/// (ADR-0029, #2608).
/// <para>
/// <b>Expected container image shape:</b> The image must bundle the Codex CLI
/// and the A2A sidecar from <c>agents/a2a-sidecar/</c>. The sidecar wraps the
/// <c>codex</c> CLI binary, exposing it behind an A2A endpoint. The container
/// must read <c>AGENTS.md</c> and <c>.mcp.json</c> from the
/// <c>SPRING_WORKSPACE_PATH</c> mount and honour the <c>OPENAI_API_KEY</c>
/// environment variable for authentication with the OpenAI API.
/// </para>
/// </summary>
public class CodexLauncher(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IAgentCallbackEnvironmentBuilder? callbackEnvironmentBuilder = null) : IAgentRuntimeLauncher
{
    /// <summary>
    /// Runtime id whose credential the Codex launcher injects. Codex uses
    /// the OpenAI Platform API, so the credential is the OpenAI API key
    /// (the same secret slot the Spring Voyage runtime uses for
    /// <c>provider: openai</c>).
    /// </summary>
    /// <summary>
    /// Provider id this launcher consumes from the catalogue's
    /// <c>(provider, authMethod)</c> edge per ADR-0038. The Codex agent
    /// runtime accepts the OpenAI provider with the API-key auth method.
    /// </summary>
    internal const string ProviderId = "openai";

    /// <summary>Container env var the Codex CLI reads its API key from.</summary>
    internal const string CredentialEnvVar = "OPENAI_API_KEY";

    /// <summary>
    /// Workspace-relative file name of the MCP config the Codex CLI reads
    /// its <c>spring-voyage</c> server definition from. ADR-0054: a single
    /// platform MCP server serves every sv.* tool. ADR-0057 §1: the CLI
    /// dials the sidecar's stdio MCP-server mode under this server name.
    /// </summary>
    internal const string McpConfigFileName = ".mcp.json";

    /// <summary>
    /// MCP server entry name — ADR-0054's single <c>spring-voyage</c>
    /// platform MCP server, now sidecar-local per ADR-0057.
    /// </summary>
    internal const string SpringVoyageMcpServerName = "spring-voyage";

    /// <summary>
    /// Argv vector the A2A bridge (agent-base ENTRYPOINT) spawns inside the
    /// Codex container on every <c>message/send</c>. Encoded as a JSON array
    /// string in <c>SPRING_AGENT_ARGV</c> (#2119) so the bridge
    /// (<c>src/Cvoya.Spring.AgentSidecar/src/config.ts</c>) can recover the
    /// exact quoting/whitespace via <c>JSON.parse</c> instead of shell
    /// splitting (#1063 history). Before this the launcher left
    /// <c>SPRING_AGENT_ARGV</c> unset, so the bridge had nothing to spawn and
    /// Codex agent containers could not execute end-to-end at all — the same
    /// defect shape #2108 fixed for Gemini.
    /// </summary>
    /// <remarks>
    /// Verified against the upstream <c>codex exec --help</c> surface
    /// (codex-cli 0.136.x):
    /// <list type="bullet">
    ///   <item><c>codex exec</c> runs Codex non-interactively. The user
    ///   prompt is read from stdin when no <c>[PROMPT]</c> argument is given
    ///   ("If not provided as an argument … instructions are read from
    ///   stdin"), which is exactly how the bridge feeds the turn — see
    ///   <c>runAgentBridge({stdin: userText})</c> in
    ///   <c>src/Cvoya.Spring.AgentSidecar/src/bridge.ts</c>. So no PROMPT
    ///   token appears in the argv.</item>
    ///   <item><c>--dangerously-bypass-approvals-and-sandbox</c> skips every
    ///   confirmation prompt and runs without Codex's own sandbox — the
    ///   container is the sandbox. Direct analogue of Claude's
    ///   <c>--dangerously-skip-permissions</c> and Gemini's <c>--yolo</c>.
    ///   Without it <c>codex exec</c> blocks on an interactive approval and
    ///   the bridge hangs.</item>
    ///   <item><c>--skip-git-repo-check</c> lets Codex run outside a Git
    ///   repository. The per-agent workspace mount is a plain volume, not a
    ///   git repo; Codex otherwise refuses (or prompts) to start there.
    ///   Belt-and-suspenders alongside the bypass flag, mirroring Gemini's
    ///   <c>--skip-trust</c>.</item>
    /// </list>
    /// <para>
    /// <b>Why plain text and not <c>--json</c>:</b> <c>codex exec</c> (no
    /// <c>--json</c>) writes the assistant's final reply to stdout as clean
    /// prose, which the sidecar's default <c>text</c> output mode forwards
    /// verbatim — so a single message dispatches end-to-end with no
    /// Codex-specific parser. <c>codex exec --json</c> emits Codex's own
    /// JSONL event stream (<c>thread.started</c> / <c>turn.started</c> /
    /// <c>item.completed</c> / <c>turn.completed</c>), which is a <i>different</i>
    /// schema from the Claude/Gemini <c>--output-format stream-json</c> shape
    /// the sidecar parses (#2226) and from Claude's single-object
    /// <c>--output-format json</c> result (#3073). Surfacing Codex's per-turn
    /// cost/usage from that JSONL is tracked separately in #3123 — until it
    /// lands, text output keeps the reply clean.
    /// </para>
    /// <para>
    /// No <c>SPRING_THREAD_ID_ARG_CREATE</c> / <c>_RESUME</c> is emitted: the
    /// catalogue's Codex <c>threadBinding</c> is <c>kind: none</c> (#2118)
    /// because Codex exposes no caller-supplied-id-at-create-time surface
    /// (<c>codex exec resume &lt;UUID&gt;</c> only resumes an already-existing
    /// session id). The bridge therefore spawns this argv unchanged and Codex
    /// cold-starts a fresh session per turn — the correct no-resume path until
    /// the Codex session-resume design call (#2122) lands.
    /// </para>
    /// </remarks>
    internal static readonly string[] BaseCodexArgv =
    [
        "codex",
        "exec",
        "--dangerously-bypass-approvals-and-sandbox",
        "--skip-git-repo-check",
    ];

    private readonly ILogger _logger = loggerFactory.CreateLogger<CodexLauncher>();

    /// <inheritdoc />
    /// <remarks>
    /// Matches the <c>launcher</c> field on the runtime catalogue's
    /// <c>codex</c> entry per ADR-0038 decision 2.
    /// </remarks>
    public string Kind => LauncherIds.CodexCli;

    /// <inheritdoc />
    /// <remarks>
    /// Verify-tool baseline only. Per-runtime credential and model probes
    /// migrate alongside the manifest reshape in PR-1b — see follow-up
    /// captured in the Chunk 2a final report; PR-1b regenerates these
    /// against the new <c>(runtime, model)</c> domain.
    /// </remarks>
    public IReadOnlyList<ProbeStep> GetProbeSteps(ModelProviderInstallConfig config, string credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new[]
        {
            new ProbeStep(
                Step: ArtefactValidationStep.VerifyingTool,
                Args: new[] { "codex", "--version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(10),
                InterpretOutput: (exit, _, stderr) => exit == 0
                    ? StepResult.Succeed()
                    : StepResult.Fail(
                        ArtefactValidationCodes.ToolMissing,
                        $"`codex --version` exited with code {exit}. {stderr}".TrimEnd())),
        };
    }

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        // #2672 / #2695: the Codex CLI has no `--system-prompt-*` flags
        // and no replace-only env var (tracked upstream at
        // openai/codex#11588). The platform's assembled system prompt
        // is delivered exclusively via Codex's auto-discovered
        // `AGENTS.md` written by ContributeBundleAsync — both Append
        // and Replace modes land at the same delivery channel. When an
        // agent declares `system_prompt_mode: replace` on a Codex
        // runtime, the field is honoured-by-best-effort only: the
        // launcher logs an informational message so operators see the
        // mismatch, and the next turn still uses `AGENTS.md`. Revisit
        // when openai/codex#11588 ships per-runtime override flags.
        //
        // The assembled-prompt path inside AgentBootstrapBundleProvider
        // already folds in the ConcurrentConversationsGuard fragment
        // (ADR-0041 / #2096) so the model still sees the concurrency
        // contract.
        if (context.SystemPromptMode == Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace)
        {
            _logger.LogInformation(
                "Codex launcher: system_prompt_mode=replace requested for agent {AgentId} " +
                "but the Codex CLI exposes no replace-only override (openai/codex#11588). " +
                "Delivering the assembled prompt via auto-discovered AGENTS.md; the CLI's " +
                "default coding-assistant baseline is preserved.",
                context.AgentId);
        }

        var workspaceMountNoSlash = AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            // #2119: the bridge parses this back into argv via JSON.parse
            // (src/Cvoya.Spring.AgentSidecar/src/config.ts) and exec's it on
            // every message/send. Without it the bridge had nothing to spawn
            // and Codex could not dispatch at all. The catalogue's Codex
            // threadBinding is `kind: none` (#2118), so the bridge appends no
            // session-id flag — Codex cold-starts a fresh session per turn.
            ["SPRING_AGENT_ARGV"] = JsonSerializer.Serialize(BaseCodexArgv),
            // ADR-0055 §5: per-member workspace mount path. ADR-0057 §3:
            // the long-running A2A sidecar writes the per-turn MCP token
            // to <SPRING_WORKSPACE_PATH>/.spring/bridge/mcp-token
            // before each CLI spawn; the per-turn sidecar-MCP-server-mode
            // child reads it from the same path.
            //
            // #3018 (Codex MCP env propagation): empirically, the Codex CLI
            // does NOT propagate its process env to the stdio MCP server it
            // spawns (verified against codex-cli 0.136.x: only the server's
            // declared `env` block reaches the child). So the per-turn
            // `SPRING_MCP_TOKEN_PATH` isolation from #3017 cannot reach a
            // Codex-spawned MCP child via env propagation — the child resolves
            // the shared per-agent token path the sidecar always (re)writes as
            // its no-regression fallback. Codex therefore does not support
            // concurrent-thread per-turn token isolation; under
            // SPRING_CONCURRENT_THREADS=true it stays on the shared-token
            // path. (Gemini, by contrast, does propagate the env — verified
            // the same way — so it needs no explicit wiring.) Carrying the
            // per-turn token to Codex via its config.toml `env` block is not
            // possible either, because that block is static config, not a
            // per-turn channel; the broader fix is tracked in #3122.
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.BuildMountPath(context.AgentId),
        };

        // ADR-0051: OTLP-ingest env contract stamped here.
        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);

        // #1714 step 2: inject the OpenAI API key into OPENAI_API_KEY.
        await ResolveRuntimeCredentialAsync(context, envVars, cancellationToken);

        _logger.LogInformation(
            "Prepared Codex launch spec for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

        return new AgentLaunchSpec(
            EnvironmentVariables: envVars,
            // #3106: pin CWD to the per-member workspace mount. The dispatcher
            // is CWD-independent (a null WorkingDirectory lets the image's
            // WORKDIR win), so a CLI launcher that discovers config relative to
            // CWD must opt in explicitly. The Codex CLI auto-discovers
            // `AGENTS.md` and `.mcp.json`, and the per-turn mcp-token, from the
            // workspace root — CWD must be that mount.
            WorkingDirectory: workspaceMountNoSlash);
    }

    /// <inheritdoc />
    /// <remarks>
    /// #2682: runtime-true prose only — names env vars and CLI surface
    /// the launcher itself wires up (workspace mount, MCP discovery
    /// file) and stays author-agnostic (no reference to the project
    /// clone, GitHub env vars, or per-task worktree conventions).
    /// </remarks>
    public string? GetWorkspacePromptFragment() =>
        """
        Your runtime is the OpenAI Codex CLI (`codex`). Your per-agent workspace is mounted at `$SPRING_WORKSPACE_PATH` and persists across turns — anything you write under it stays available next turn. The CLI auto-discovers its system prompt from `AGENTS.md` at the workspace root and its MCP server set from `.mcp.json` (also at the workspace root).
        """;

    /// <inheritdoc />
    public Task<AgentBootstrapContribution> ContributeBundleAsync(
        AgentBootstrapContributionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ADR-0057 §§1, 3: the sidecar stdio MCP-server-mode `command`-typed
        // server entry. See ClaudeCodeLauncher.ContributeBundleAsync for the
        // full rationale; identical shape, only the server-name reuse and the
        // sidecar binary path are shared. No HTTP transport, no Authorization
        // header — the CLI never sees the per-turn token.
        //
        // KNOWN GAP (#3122): the Codex CLI does NOT read `.mcp.json` — it
        // discovers MCP servers from `$CODEX_HOME/config.toml`
        // `[mcp_servers.<name>]` (verified against codex-cli 0.136.x). So this
        // `.mcp.json` is currently inert for Codex and the platform sv.* tools
        // are not yet wired for the `codex` runtime. Writing it here keeps the
        // bundle shape parallel with Claude/Gemini until #3122 moves the Codex
        // MCP wiring to its real config.toml surface; it is harmless (an unread
        // file in the workspace), not load-bearing.
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [SpringVoyageMcpServerName] = new
                {
                    command = ClaudeCodeLauncher.SidecarNodeBinary,
                    args = new[] { ClaudeCodeLauncher.SidecarBinaryPath, "mcp" },
                    env = new Dictionary<string, string>
                    {
                        ["SPRING_MCP_PROXY_URL"] = context.McpEndpoint,
                        ["SPRING_WORKSPACE_PATH"] =
                            AgentWorkspaceContract.BuildMountPath(context.AgentId),
                    },
                },
            },
        };

        // AGENTS.md is the Codex CLI's auto-discovered system-prompt
        // file. The bundle provider has composed the per-agent system
        // prompt (platform contract + unit context + agent
        // instructions + equipped skill bundles) via IPromptAssembler
        // and handed it in on AgentBootstrapContributionContext.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTS.md"] = context.AssembledSystemPrompt,
            [McpConfigFileName] = JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true }),
        };

        return Task.FromResult(new AgentBootstrapContribution(
            Files: files,
            PlatformFilePaths: new[] { "AGENTS.md", McpConfigFileName }));
    }

    private async Task ResolveRuntimeCredentialAsync(
        AgentLaunchContext context,
        IDictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
        Guid? agentGuid = Guid.TryParse(context.AgentId, out var parsedAgentId)
            ? parsedAgentId
            : null;
        Guid? unitGuid = Guid.TryParse(context.UnitId, out var parsedUnitId)
            ? parsedUnitId
            : null;

        // Per-call scope so the scoped resolver works from this singleton.
        await using var scope = scopeFactory.CreateAsyncScope();
        var credentialResolver = scope.ServiceProvider
            .GetRequiredService<ILlmCredentialResolver>();
        // ADR-0038 (#1770): the resolver is keyed on (provider, authMethod).
        // The Codex runtime consumes OpenAI via API key.
        var resolution = await credentialResolver.ResolveAsync(
            ProviderId, AuthMethod.ApiKey, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            // #2189: tag (CredentialMissing, credential).
            throw new SpringException(
                $"Codex agent runtime requires secret '{resolution.SecretName}' but no value resolved at " +
                $"agent, unit, parent-unit chain, or tenant scope. " +
                $"Generate an API key at https://platform.openai.com/api-keys and store it under '{resolution.SecretName}', " +
                $"or configure via the Tenant defaults panel.")
                .WithIssue(code: "CredentialMissing", source: "credential");
        }

        // OpenAI Platform API keys start with "sk-". The format check is
        // inline in the launcher per ADR-0038 — the agent-runtime path
        // owns its own per-path acceptance rules; the REST path's
        // equivalent lives on IModelProviderAdapter.
        if (!resolution.Value!.StartsWith("sk-", StringComparison.Ordinal))
        {
            // #2189: tag (CredentialFormatRejected, credential).
            throw new SpringException(
                $"Codex agent runtime did not accept the configured '{resolution.SecretName}' value at scope " +
                $"'{resolution.Source}'. The OpenAI Platform CLI path requires an OpenAI API key (sk-…).")
                .WithIssue(code: "CredentialFormatRejected", source: "credential");
        }

        envVars[CredentialEnvVar] = resolution.Value!;

        _logger.LogInformation(
            "Codex credential resolved from {Source} into {EnvVar} for agent {AgentId}",
            resolution.Source, CredentialEnvVar, context.AgentId);
    }
}
