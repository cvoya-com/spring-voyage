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
///   <item><c>CLAUDE.md</c> — the assembled system prompt (all four layers).</item>
///   <item><c>.mcp.json</c> — MCP server endpoint + bearer token Claude Code will dial.</item>
/// </list>
/// These files are written into the per-agent persistent workspace volume —
/// the single workspace mount at <see cref="AgentWorkspaceContract.WorkspaceMountPath"/>
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
    /// </list>
    /// <para>
    /// <b>Why no <c>--output-format stream-json</c>:</b> the Claude CLI rejects
    /// <c>--print --output-format stream-json</c> without a companion
    /// <c>--verbose</c>, and even if both are passed the A2A sidecar
    /// (<c>src/Cvoya.Spring.AgentSidecar/src/bridge.ts</c>) currently forwards
    /// stdout verbatim into the A2A response body — the user would see raw
    /// NDJSON instead of the assistant's reply. Plain text output is what the
    /// bridge actually consumes today; re-enabling stream-json (with the
    /// matching parser on the sidecar side, so events become
    /// <see cref="Cvoya.Spring.Core.Messaging.StreamEvent"/>s) is tracked in
    /// issue #2226.
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
    /// to the CLI's working directory. The dispatcher now sets the
    /// container's WORKDIR to the per-member workspace mount, but
    /// pinning the path on argv makes the wiring robust against any
    /// future CWD drift (and against runtimes that re-exec from a
    /// different directory). Without this, the CLI starts with zero MCP
    /// tools — the bug that motivated this flag.
    /// </para>
    /// </remarks>
    internal static readonly string[] BaseClaudeArgv =
    [
        "claude",
        "--print",
        "--dangerously-skip-permissions"
    ];

    /// <summary>
    /// Builds the argv vector exec'd by the bridge on every
    /// <c>message/send</c>: <see cref="BaseClaudeArgv"/> followed by
    /// <c>--mcp-config &lt;path&gt;</c> so the CLI loads the
    /// platform's <c>spring-voyage</c> MCP server regardless of CWD.
    /// </summary>
    internal static string[] BuildClaudeArgv(string mcpConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpConfigPath);
        return [.. BaseClaudeArgv, "--mcp-config", mcpConfigPath];
    }

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
        // #2668: the Claude Code CLI never reads SPRING_SYSTEM_PROMPT —
        // the system prompt is delivered exclusively via CLAUDE.md written
        // by ContributeBundleAsync, and the assembled-prompt path inside
        // AgentBootstrapBundleProvider already folds in the
        // ConcurrentThreadsGuard fragment (ADR-0041 / #2096) so the model
        // still sees the concurrency contract. Nothing the launcher could
        // stamp here would reach the CLI.

        var workspaceMountNoSlash = AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId);
        var mcpConfigPath = $"{workspaceMountNoSlash}/{McpConfigFileName}";

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            // The bridge parses this back into argv via JSON.parse — see
            // src/Cvoya.Spring.AgentSidecar/src/config.ts. The argv carries
            // `--mcp-config <path>` so the CLI always loads the platform
            // MCP server, regardless of the bridge's spawn CWD.
            ["SPRING_AGENT_ARGV"] = JsonSerializer.Serialize(BuildClaudeArgv(mcpConfigPath)),
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
            // ADR-0055 §5: per-member workspace mount path. ADR-0057 §3:
            // the long-running A2A sidecar writes the per-turn MCP token
            // to <SPRING_WORKSPACE_PATH>/.spring-voyage-bridge/mcp-token
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
            Argv: Array.Empty<string>());
    }

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
        // <workspace>/.spring-voyage-bridge/mcp-token (written by the
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

        // CLAUDE.md is Claude Code's auto-discovered project context file
        // — the only system-prompt surface the CLI reads at the
        // workspace level. The bundle provider has already composed the
        // per-agent system prompt (platform contract + unit context +
        // agent instructions + equipped skill bundles) via
        // IPromptAssembler and handed it in on
        // AgentBootstrapContributionContext.AssembledSystemPrompt;
        // writing Definition.Instructions raw here would drop the
        // platform contract and leave the CLI silently dispatching.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLAUDE.md"] = context.AssembledSystemPrompt,
            [".mcp.json"] = SerializeMcpConfig(mcpConfig),
        };

        return Task.FromResult(new AgentBootstrapContribution(
            Files: files,
            PlatformFilePaths: new[] { "CLAUDE.md", ".mcp.json" }));
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
