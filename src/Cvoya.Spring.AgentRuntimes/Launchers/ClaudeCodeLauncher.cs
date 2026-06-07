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
/// <see cref="IAgentRuntimeLauncher"/> for Claude Code containers. Describes a
/// per-invocation workspace containing:
/// <list type="bullet">
///   <item><c>.spring/system-prompt.md</c> — the assembled system prompt (all
///         four layers). Written under the <c>.spring/</c> namespace per
///         ADR-0058 §2.2.2 so it does not collide with any project clone's
///         own <c>CLAUDE.md</c> (the engineer agents clone projects whose
///         roots carry that filename — #2672). The Claude CLI is pointed at
///         the file via <c>--append-system-prompt-file</c> (Append mode,
///         the default) or <c>--system-prompt-file</c> (Replace mode) per
///         <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode"/>
///         (#2695). Both flags consume a workspace-resolved file path —
///         see <see cref="BuildClaudeArgv"/>.</item>
///   <item><c>.mcp.json</c> — MCP server endpoint + bearer token Claude Code will dial.</item>
/// </list>
/// These files are written into the per-agent persistent workspace volume —
/// the single workspace mount at <see cref="AgentWorkspaceContract.WorkspacePathEnvVar"/>
/// (ADR-0029, #2608) — so they sit alongside the CLI's session state, the
/// bridge's marker files, and everything else <c>SPRING_WORKSPACE_PATH</c>
/// points at.
///
/// PR 4 of the #1087 series wires the launcher to the BYOI conformance
/// path 1: the spec leaves <see cref="AgentLaunchSpec.Argv"/> empty so the
/// agent-base image's ENTRYPOINT (the TypeScript A2A bridge) takes over and
/// re-execs the real CLI from <c>SPRING_AGENT_ARGV</c>. The launcher also
/// surfaces the assembled prompt as <see cref="AgentLaunchSpec.StdinPayload"/>
/// so PR 5 can flow it through the bridge to <c>claude</c>'s stdin.
/// </summary>
public class ClaudeCodeLauncher(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IAgentCallbackEnvironmentBuilder? callbackEnvironmentBuilder = null) : IAgentRuntimeLauncher
{
    /// <summary>
    /// Provider id this launcher consumes from the catalogue's
    /// <c>(provider, authMethod)</c> edge per ADR-0038. The Claude agent
    /// runtime accepts the Anthropic provider with the OAuth auth method.
    /// </summary>
    internal const string ProviderId = "anthropic";

    /// <summary>
    /// Container env var the Claude Code CLI reads its OAuth token from.
    /// The Claude agent-runtime path is OAuth-only (#1714); API keys are
    /// rejected pre-flight here and only flow via the Spring Voyage REST
    /// path's <c>ANTHROPIC_API_KEY</c>.
    /// </summary>
    internal const string CredentialEnvVar = "CLAUDE_CODE_OAUTH_TOKEN";

    // ADR-0054: a single platform MCP server serves every sv.* tool —
    // sv.messaging.* included. ADR-0057 §1: the CLI dials the sidecar's
    // stdio MCP-server mode under this name; the sidecar proxies onto
    // the worker's McpServer.
    internal const string SpringVoyageMcpServerName = "spring-voyage";

    /// <summary>
    /// Workspace-relative file name of the MCP config the Claude Code CLI
    /// reads its <c>spring-voyage</c> server definition from.
    /// </summary>
    internal const string McpConfigFileName = ".mcp.json";

    /// <summary>
    /// Workspace-relative path the platform writes the assembled system
    /// prompt to (#2672). Lives under the <c>.spring/</c> namespace per
    /// ADR-0058 §2.2.2 so it does not collide with any project clone's own
    /// <c>CLAUDE.md</c>. The Claude CLI is pointed at this path via
    /// <see cref="AppendSystemPromptFileFlag"/> (default) or
    /// <see cref="SystemPromptFileFlag"/> (replace) on every spawn — see
    /// <see cref="BuildClaudeArgv"/>.
    /// </summary>
    internal const string PlatformPromptFilePath = ".spring/system-prompt.md";

    /// <summary>
    /// Claude Code CLI flag that points the model at a file whose contents
    /// are appended to the CLI's own default coding-assistant system
    /// prompt. Verified against <c>claude --help</c> (2.1.x). Used when
    /// <see cref="AgentLaunchContext.SystemPromptMode"/> is
    /// <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Append"/> —
    /// the default for engineer-shaped agents (#2695).
    /// </summary>
    internal const string AppendSystemPromptFileFlag = "--append-system-prompt-file";

    /// <summary>
    /// Claude Code CLI flag that replaces the CLI's own default system
    /// prompt with the contents of the named file. Verified against
    /// <c>claude --help</c> (2.1.x). Used when
    /// <see cref="AgentLaunchContext.SystemPromptMode"/> is
    /// <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace"/> —
    /// non-coding agents (routers, PMs) opt in so the CLI's
    /// coding-assistant baseline does not shape responses (#2695).
    /// Mutually exclusive with <see cref="AppendSystemPromptFileFlag"/>;
    /// the launcher emits exactly one.
    /// </summary>
    internal const string SystemPromptFileFlag = "--system-prompt-file";

    /// <summary>
    /// Absolute container path of the sidecar binary the CLI spawns as a
    /// stdio MCP server (ADR-0057 §1). The agent-base image installs the
    /// compiled sidecar bundle under <c>/opt/spring-voyage/sidecar/dist/cli.js</c>;
    /// the <c>.mcp.json</c> we write for Claude Code names this command
    /// + the <c>mcp</c> argv token so each CLI tool-use round spawns a
    /// per-turn stdio MCP server that proxies onto the worker.
    /// </summary>
    internal const string SidecarBinaryPath = "/opt/spring-voyage/sidecar/dist/cli.js";

    /// <summary>
    /// argv[0] for the sidecar-MCP-server-mode spawn. The CLI invokes
    /// <c>node /opt/.../cli.js mcp</c>; <c>node</c> is on PATH inside
    /// the agent-base image (installed via nodesource).
    /// </summary>
    internal const string SidecarNodeBinary = "node";

    /// <summary>
    /// Bridge env var name carrying the CLI flag that *creates* a session
    /// with a supplied id (ADR-0041 / #2094). Read by
    /// `src/Cvoya.Spring.AgentSidecar/src/config.ts:parseThreadBinding`.
    /// </summary>
    internal const string ThreadIdArgCreateEnvVar = "SPRING_THREAD_ID_ARG_CREATE";

    /// <summary>
    /// Bridge env var name carrying the CLI flag that *resumes* a session
    /// by id (ADR-0041 / #2094).
    /// </summary>
    internal const string ThreadIdArgResumeEnvVar = "SPRING_THREAD_ID_ARG_RESUME";

    /// <summary>
    /// Claude Code CLI flag that creates a fresh session with a caller-
    /// supplied UUID. Verified against `claude --help` (2.1.x): the flag
    /// is documented as <c>--session-id &lt;uuid&gt;</c>.
    /// </summary>
    internal const string ThreadIdArgCreate = "--session-id";

    /// <summary>
    /// Claude Code CLI flag that resumes a session by id. Verified
    /// against `claude --help` (2.1.x): <c>-r, --resume [value]</c>.
    /// </summary>
    internal const string ThreadIdArgResume = "--resume";

    /// <summary>
    /// Env var Claude Code reads to locate its config / session storage
    /// directory. When set, Claude writes session files under
    /// <c>$CLAUDE_CONFIG_DIR/projects/&lt;cwd-mangled&gt;/&lt;sid&gt;.jsonl</c>;
    /// when unset it falls back to <c>~/.claude/</c>. Pointing this under
    /// <see cref="AgentWorkspaceContract.WorkspaceMountPath"/> is what
    /// makes session resume work across container restarts.
    /// </summary>
    internal const string ClaudeConfigDirEnvVar = "CLAUDE_CONFIG_DIR";

    /// <summary>
    /// Relative path under <see cref="AgentWorkspaceContract.WorkspaceMountPath"/>
    /// the launcher hands to Claude Code as its config dir. Using a
    /// dot-prefixed name keeps it out of agent-author tools that walk
    /// the workspace tree for sources.
    /// </summary>
    internal const string ClaudeConfigDirRelative = ".claude";

    /// <summary>
    /// Env var that disables Claude Code's native <i>auto-memory</i> — the
    /// cross-session feature where the model accumulates notes under
    /// <c>&lt;CLAUDE_CONFIG_DIR&gt;/projects/&lt;cwd-mangled&gt;/memory/</c>
    /// (a <c>MEMORY.md</c> index plus topic files, auto-loaded at session
    /// start). Verified against the Claude Code docs: the feature ships
    /// since CLI v2.1.59 and is on by default, so it is active on the
    /// pinned 2.1.x agent image unless turned off here.
    /// </summary>
    /// <remarks>
    /// ADR-0065 keystone: <c>sv.memory.*</c> is the canonical durable
    /// cross-thread store; native file memory is per-session scratch only.
    /// Auto-memory is keyed by the git repo / cwd, so every thread of one
    /// agent — which share a single workspace volume, cwd, and
    /// <see cref="ClaudeConfigDirEnvVar"/> — would share <b>one</b> memory
    /// directory. That shared dir is two defects at once: under
    /// <c>concurrent_threads: true</c> concurrent turns collide on the same
    /// files with "File has been modified since read" (#2982 finding D),
    /// and a thread-private note leaks into every other thread's recall
    /// while being silently lost on a volume reclaim (#2999) — the
    /// false-durability trap behind the #2980 incident's self-disavowal.
    /// Disabling auto-memory leaves durable state with exactly one home
    /// (<c>sv.memory.*</c>, advertised by the #2984 Platform-Contract
    /// clause). It is distinct from the per-thread session transcript
    /// (<c>&lt;sid&gt;.jsonl</c>, keyed by <c>thread.id</c>) — a separate
    /// knob (<c>CLAUDE_CODE_SKIP_PROMPT_HISTORY</c>) governs that — so
    /// within-thread <c>--resume</c> continuity is preserved. #2985.
    /// </remarks>
    internal const string DisableAutoMemoryEnvVar = "CLAUDE_CODE_DISABLE_AUTO_MEMORY";

    /// <summary>
    /// Env var the A2A sidecar reads to learn how to interpret the CLI's
    /// stdout (#3073 / <c>src/Cvoya.Spring.AgentSidecar/src/config.ts</c>).
    /// Set to <see cref="OutputFormatJson"/> here because
    /// <see cref="BaseClaudeArgv"/> runs <c>claude --output-format json</c>;
    /// the sidecar then surfaces the result's <c>.result</c> prose as the
    /// reply and hands <c>total_cost_usd</c> + <c>usage</c> to the host as
    /// A2A task metadata so the cost ledger / budget enforcer get real
    /// numbers. The bridge stays agent-agnostic — it never parses CLI flags.
    /// </summary>
    internal const string OutputFormatEnvVar = "SPRING_AGENT_OUTPUT_FORMAT";

    /// <summary>JSON output-format hint value for <see cref="OutputFormatEnvVar"/>.</summary>
    internal const string OutputFormatJson = "json";

    /// <summary>
    /// Argv vector the A2A bridge (agent-base ENTRYPOINT) spawns inside the
    /// container on every <c>message/send</c>. Encoded as a JSON array string
    /// in <c>SPRING_AGENT_ARGV</c> so the bridge can recover the exact
    /// quoting/whitespace without shell-splitting (see #1063 for why we
    /// avoid string-split argv).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>--print</c> drives <c>claude</c> in non-interactive mode so
    ///   it consumes stdin and writes to stdout instead of opening a TUI.</item>
    ///   <item><c>--dangerously-skip-permissions</c> waives the per-tool
    ///   confirmation prompt — the container is the sandbox.</item>
    ///   <item><c>--output-format json</c> makes <c>claude</c> emit a single
    ///   JSON result object (reply text + <c>total_cost_usd</c> + <c>usage</c>)
    ///   instead of bare prose. The sidecar (gated by
    ///   <see cref="OutputFormatEnvVar"/>) parses it, surfaces the
    ///   <c>.result</c> prose as the reply, and hands the cost/usage back to
    ///   the host so budget tracking is wired end-to-end (#3073).</item>
    /// </list>
    /// <para>
    /// <b>Why <c>--output-format json</c> and not <c>stream-json</c>:</b> the
    /// single-object <c>json</c> form does not require the <c>--verbose</c>
    /// companion that <c>--print --output-format stream-json</c> does, and the
    /// sidecar parses one object rather than an NDJSON event stream. The
    /// richer per-event streaming form (assistant deltas, tool-use surfacing)
    /// is tracked separately in issue #2226; when it lands it can read the
    /// same terminal <c>result</c> fields for cost.
    /// </para>
    /// <para>
    /// Source: BYOI path-1 baseline documented in #1097. Since PR 5 of #1087
    /// (#1098) the dispatcher no longer runs <c>sleep infinity</c>: the argv
    /// below is JSON-encoded into <c>SPRING_AGENT_ARGV</c> and exec'd by the
    /// agent-base bridge on every <c>message/send</c>, with the user's prompt
    /// fed via stdin.
    /// </para>
    /// <para>
    /// <b>Why <c>--mcp-config</c> is passed explicitly:</b> Claude Code
    /// discovers project-scoped MCP servers from <c>.mcp.json</c> relative
    /// to the CLI's working directory. This launcher pins the container's
    /// WORKDIR to the per-member workspace mount via
    /// <see cref="AgentLaunchSpec.WorkingDirectory"/> (#3106 — the dispatcher
    /// is otherwise CWD-independent), but pinning the path on argv too makes
    /// the wiring robust against any CWD drift (and against runtimes that
    /// re-exec from a different directory). Without this, the CLI starts with
    /// zero MCP tools — the bug that motivated this flag.
    /// </para>
    /// </remarks>
    internal static readonly string[] BaseClaudeArgv =
    [
        "claude",
        "--print",
        "--dangerously-skip-permissions",
        "--output-format", "json"
    ];

    /// <summary>
    /// Builds the argv vector exec'd by the bridge on every
    /// <c>message/send</c>: <see cref="BaseClaudeArgv"/> followed by
    /// <c>--mcp-config &lt;path&gt;</c> so the CLI loads the
    /// platform's <c>spring-voyage</c> MCP server regardless of CWD,
    /// plus the system-prompt-file flag selected by
    /// <see cref="MapSystemPromptModeToFlag"/>. The CLI's
    /// <c>--append-system-prompt-file</c> and <c>--system-prompt-file</c>
    /// flags are mutually exclusive (Claude CLI reference); the launcher
    /// emits exactly one (#2695).
    /// </summary>
    internal static string[] BuildClaudeArgv(
        string mcpConfigPath,
        string systemPromptFlag,
        string systemPromptFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPromptFlag);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPromptFilePath);
        return
        [
            .. BaseClaudeArgv,
            "--mcp-config", mcpConfigPath,
            systemPromptFlag, systemPromptFilePath,
        ];
    }

    /// <summary>
    /// Maps a resolved <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode"/>
    /// to the Claude CLI flag that carries that semantics (#2695).
    /// <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Append"/>
    /// preserves the CLI's coding-assistant default plus the platform
    /// prompt; <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace"/>
    /// drops the CLI default entirely (the platform prompt becomes the
    /// whole system prompt).
    /// </summary>
    internal static string MapSystemPromptModeToFlag(SystemPromptMode mode) => mode switch
    {
        SystemPromptMode.Replace => SystemPromptFileFlag,
        SystemPromptMode.Append => AppendSystemPromptFileFlag,
        _ => AppendSystemPromptFileFlag,
    };

    private readonly ILogger _logger = loggerFactory.CreateLogger<ClaudeCodeLauncher>();

    /// <inheritdoc />
    /// <remarks>
    /// Matches the <c>launcher</c> field on the runtime catalogue's
    /// <c>claude-code</c> entry per ADR-0038 decision 2.
    /// </remarks>
    public string Kind => LauncherIds.ClaudeCodeCli;

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
                Args: new[] { "claude", "--version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(10),
                InterpretOutput: (exit, _, stderr) => exit == 0
                    ? StepResult.Succeed()
                    : StepResult.Fail(
                        ArtefactValidationCodes.ToolMissing,
                        $"`claude --version` exited with code {exit}. {stderr}".TrimEnd())),
        };
    }

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        // #2672 / #2695: the Claude Code CLI receives the platform's
        // assembled system prompt via the
        // `--append-system-prompt-file` / `--system-prompt-file` flag on
        // every spawn (mode chosen by context.SystemPromptMode). The
        // assembled-prompt path inside AgentBootstrapBundleProvider
        // already folds in the ConcurrentConversationsGuard fragment
        // (ADR-0041 / #2096) so the model still sees the concurrency
        // contract. The platform no longer writes to the CLI's
        // auto-discovered `CLAUDE.md` filename — that's reserved for any
        // project clone the agent makes under its workspace (e.g.
        // `<workspace>/myrepo/CLAUDE.md`), which the CLI walks up from
        // its cwd as project context.

        var workspaceMountNoSlash = AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId);
        var mcpConfigPath = $"{workspaceMountNoSlash}/{McpConfigFileName}";
        var systemPromptFilePath = $"{workspaceMountNoSlash}/{PlatformPromptFilePath}";
        // #2695: select the Claude CLI flag for the resolved
        // system_prompt_mode. The agent → unit → Append cascade has
        // already been applied at the dispatch site
        // (A2AExecutionDispatcher); the launcher consumes
        // context.SystemPromptMode directly without further fallback.
        var systemPromptFlag = MapSystemPromptModeToFlag(context.SystemPromptMode);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            // The bridge parses this back into argv via JSON.parse — see
            // src/Cvoya.Spring.AgentSidecar/src/config.ts. The argv carries
            // `--mcp-config <path>` so the CLI always loads the platform
            // MCP server, regardless of the bridge's spawn CWD, plus the
            // system-prompt-file flag (#2672 / #2695): either
            // `--append-system-prompt-file` (Append mode — default) or
            // `--system-prompt-file` (Replace mode), both pointing at
            // `<workspace>/.spring/system-prompt.md` written by
            // ContributeBundleAsync.
            ["SPRING_AGENT_ARGV"] = JsonSerializer.Serialize(
                BuildClaudeArgv(mcpConfigPath, systemPromptFlag, systemPromptFilePath)),
            // ADR-0041 / #2094: tell the bridge how to bind the platform
            // thread.id (= A2A 0.3 contextId) onto Claude Code's session
            // identifier.
            [ThreadIdArgCreateEnvVar] = ThreadIdArgCreate,
            [ThreadIdArgResumeEnvVar] = ThreadIdArgResume,
            // ADR-0041 / #2094: anchor Claude Code's session storage on the
            // per-member workspace volume (ADR-0055 §5). Claude writes
            // session files at
            // <CLAUDE_CONFIG_DIR>/projects/<cwd-mangled>/<sid>.jsonl;
            // pointing CLAUDE_CONFIG_DIR under SPRING_WORKSPACE_PATH means
            // the session file survives container restart and can be
            // resumed by the next --resume <sid> invocation.
            [ClaudeConfigDirEnvVar] = $"{workspaceMountNoSlash}/{ClaudeConfigDirRelative}",
            // #2985 / ADR-0065: turn off Claude Code's native auto-memory so
            // durable cross-thread state has exactly one home (sv.memory.*)
            // and concurrent threads can't collide on the single per-repo
            // memory dir (#2982 finding D). The per-thread <sid>.jsonl
            // transcript is unaffected, so --resume continuity within a
            // thread is preserved. See DisableAutoMemoryEnvVar for the full
            // rationale.
            [DisableAutoMemoryEnvVar] = "1",
            // #3073: BaseClaudeArgv runs `claude --output-format json`, so tell
            // the sidecar to parse the JSON result — surface `.result` as the
            // reply and forward the turn's cost/usage to the host. Paired with
            // the `--output-format json` argv tokens above.
            [OutputFormatEnvVar] = OutputFormatJson,
            // ADR-0055 §5: per-member workspace mount path. ADR-0057 §3:
            // the long-running A2A sidecar writes the per-turn MCP token
            // to <SPRING_WORKSPACE_PATH>/.spring/bridge/mcp-token
            // before each CLI spawn; the per-turn sidecar-MCP-server-mode
            // child reads it from the same path.
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.BuildMountPath(context.AgentId),
        };

        // ADR-0051: OTLP-ingest env contract still stamped here.
        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);

        // #1714 step 2: inject the Claude OAuth token into
        // CLAUDE_CODE_OAUTH_TOKEN. The Claude agent runtime is OAuth-only.
        await ResolveRuntimeCredentialAsync(context, envVars, cancellationToken);

        _logger.LogInformation(
            "Prepared Claude Code launch spec for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

        return new AgentLaunchSpec(
            EnvironmentVariables: envVars,
            // Empty argv: defer to the agent-base image's ENTRYPOINT (the
            // TypeScript bridge), which reads SPRING_AGENT_ARGV and spawns
            // the real CLI per message/send. BYOI conformance path 1.
            Argv: Array.Empty<string>(),
            // #3106: pin CWD to the per-member workspace mount. The dispatcher
            // is CWD-independent (a null WorkingDirectory lets the image's
            // WORKDIR win), so a CLI launcher that discovers config relative to
            // CWD must opt in explicitly. Claude Code reads `.mcp.json`, its
            // `.claude/` session store, and the per-turn mcp-token from the
            // workspace root, so CWD must be that mount.
            WorkingDirectory: workspaceMountNoSlash);
    }

    /// <inheritdoc />
    /// <remarks>
    /// #2682: runtime-true prose only — names env vars and CLI surface
    /// the launcher itself wires up (workspace mount, MCP discovery
    /// file, session-storage env var) and stays author-agnostic (no
    /// reference to the project clone, GitHub env vars, or per-task
    /// worktree conventions). Per ADR-0058 §2.2 the platform's
    /// system-prompt file lives under the `.spring/` namespace; the CLI
    /// auto-discovery files (`.mcp.json`, `.claude/`) live at the
    /// workspace root per ADR-0058 §2.2.1. The platform prompt reaches
    /// the CLI via `--append-system-prompt-file` / `--system-prompt-file`
    /// on argv, not via auto-discovery, so any project clone's own
    /// `CLAUDE.md` (e.g. `<workspace>/myrepo/CLAUDE.md`) is the only
    /// `CLAUDE.md` the CLI walks up from its cwd as project context.
    /// </remarks>
    public string? GetWorkspacePromptFragment() =>
        """
        Your runtime is the Claude Code CLI (`claude`). Your per-agent workspace is mounted at `$SPRING_WORKSPACE_PATH` and persists across turns — anything you write under it stays available next turn. The platform's system prompt is delivered to the CLI on every spawn via `--append-system-prompt-file` (or `--system-prompt-file` when an agent declares `system_prompt_mode: replace`); the CLI also auto-discovers its MCP server set from `.mcp.json` at the workspace root, and per-thread session state lives under `$CLAUDE_CONFIG_DIR`.
        """;

    /// <inheritdoc />
    public Task<AgentBootstrapContribution> ContributeBundleAsync(
        AgentBootstrapContributionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ADR-0057 §§1, 3: the CLI dials the sidecar's stdio MCP-server
        // mode as a `command`-typed MCP server. Each tool-use round
        // spawns `node /opt/.../cli.js mcp` as a child of the CLI; that
        // child reads the per-turn MCP session token from
        // <workspace>/.spring/bridge/mcp-token (written by the
        // long-running A2A sidecar from A2A message/send metadata) and
        // proxies tools/list, tools/call, and initialize onto the
        // worker's POST /mcp/ route — passing
        // <see cref="SpringVoyageMcpServerName"/>'s only client through
        // the per-agent trust boundary. No HTTP transport, no
        // Authorization header in this config — the CLI never sees the
        // per-turn token.
        //
        // SPRING_MCP_PROXY_URL points the spawned MCP-server-mode child
        // at the worker's MCP endpoint; the launcher knows the endpoint
        // from <see cref="AgentBootstrapContributionContext.McpEndpoint"/>
        // and stamps it on the env block of this stdio MCP server. The
        // sidecar reads it at MCP-server startup.
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                [SpringVoyageMcpServerName] = new
                {
                    command = SidecarNodeBinary,
                    args = new[] { SidecarBinaryPath, "mcp" },
                    env = new Dictionary<string, string>
                    {
                        ["SPRING_MCP_PROXY_URL"] = context.McpEndpoint,
                        // The MCP-server-mode child reads the per-turn
                        // token from this workspace path; passing it
                        // explicitly avoids relying on the CLI to
                        // propagate SPRING_WORKSPACE_PATH into the
                        // spawn env (Claude Code does, but the
                        // contract is clearer if the env is local to
                        // this server entry).
                        ["SPRING_WORKSPACE_PATH"] =
                            AgentWorkspaceContract.BuildMountPath(context.AgentId),
                    },
                },
            },
        };

        // #2672: write the platform's assembled system prompt to
        // `.spring/system-prompt.md` under the workspace root rather than
        // the CLI's auto-discovered `CLAUDE.md` filename. The
        // `.spring/` namespace is ADR-0058 §2.2.2's home for every
        // platform-managed file where the platform owns the name; it
        // keeps the platform contract from colliding with any project
        // clone the agent makes under its workspace (engineer agents
        // clone projects whose roots carry their own `CLAUDE.md`).
        //
        // The CLI is pointed at this file via the
        // `--append-system-prompt-file` / `--system-prompt-file` flag on
        // argv (see BuildClaudeArgv) — auto-discovery is bypassed
        // entirely.
        //
        // The bundle provider has already composed the per-agent system
        // prompt (platform contract + unit context + agent instructions
        // + equipped skill bundles) via IPromptAssembler and handed it
        // in on AgentBootstrapContributionContext.AssembledSystemPrompt;
        // writing Definition.Instructions raw here would drop the
        // platform contract and leave the CLI silently dispatching.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PlatformPromptFilePath] = context.AssembledSystemPrompt,
            [".mcp.json"] = SerializeMcpConfig(mcpConfig),
        };

        return Task.FromResult(new AgentBootstrapContribution(
            Files: files,
            PlatformFilePaths: new[] { PlatformPromptFilePath, ".mcp.json" }));
    }

    private static string SerializeMcpConfig(object mcpConfig) =>
        JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true });

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
        // The Claude agent runtime consumes Anthropic via OAuth — the
        // catalogue edge `claude-code → anthropic` carries authMethod: oauth.
        var resolution = await credentialResolver.ResolveAsync(
            ProviderId, AuthMethod.Oauth, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            // #2189: tag (CredentialMissing, credential) so the
            // AgentActor catch attributes this precisely instead of
            // falling back to source="runtime".
            throw new SpringException(
                $"Claude agent runtime requires secret '{resolution.SecretName}' but no value resolved at " +
                $"agent, unit, parent-unit chain, or tenant scope. " +
                $"Generate an OAuth token via `claude setup-token` and store it under '{resolution.SecretName}', " +
                $"or configure via the Tenant defaults panel.")
                .WithIssue(code: "CredentialMissing", source: "credential");
        }

        // Strict per-path acceptance (#1714): the Claude CLI dispatch
        // path is OAuth-only. Reject API keys with operator guidance —
        // they are usable on this project only via `agent: spring-voyage,
        // provider: anthropic` (which routes through Dapr Conversation
        // REST and accepts API keys exclusively). The format check is
        // inline here per ADR-0038 — the agent-runtime path owns its own
        // per-path acceptance rules; the REST path's equivalent lives
        // on IModelProviderAdapter.
        if (!resolution.Value!.StartsWith("sk-ant-oat", StringComparison.Ordinal))
        {
            // #2189: tag (CredentialFormatRejected, credential).
            throw new SpringException(
                $"Claude agent runtime requires an OAuth token generated by `claude setup-token`. " +
                $"The configured '{resolution.SecretName}' value at scope '{resolution.Source}' is not in OAuth shape. " +
                $"Either regenerate via `claude setup-token` and update the secret, or switch this agent to " +
                $"`agent: spring-voyage, provider: anthropic` (which accepts API keys via the Dapr Conversation REST path).")
                .WithIssue(code: "CredentialFormatRejected", source: "credential");
        }

        envVars[CredentialEnvVar] = resolution.Value!;

        _logger.LogInformation(
            "Claude credential resolved from {Source} into {EnvVar} for agent {AgentId}",
            resolution.Source, CredentialEnvVar, context.AgentId);
    }
}
