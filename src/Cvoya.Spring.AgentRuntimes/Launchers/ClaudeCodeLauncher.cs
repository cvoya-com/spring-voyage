// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
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
/// The dispatcher materialises this workspace on its own host filesystem and
/// bind-mounts it at <c>/workspace</c> inside the container — see issue #1042.
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

    internal const string WorkspaceMountPath = "/workspace";

    internal const string SpringVoyageMcpServerName = "spring-voyage";

    internal const string SpringOrchestrationMcpServerName = "spring-orchestration";

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
    /// </remarks>
    internal static readonly string[] DefaultClaudeArgv =
    [
        "claude",
        "--print",
        "--dangerously-skip-permissions"
    ];

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
                Step: UnitValidationStep.VerifyingTool,
                Args: new[] { "claude", "--version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(10),
                InterpretOutput: (exit, _, stderr) => exit == 0
                    ? StepResult.Succeed()
                    : StepResult.Fail(
                        UnitValidationCodes.ToolMissing,
                        $"`claude --version` exited with code {exit}. {stderr}".TrimEnd())),
        };
    }

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        var mcpServers = new Dictionary<string, object>
        {
            [SpringVoyageMcpServerName] = new
            {
                type = "http",
                url = context.McpEndpoint,
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {context.McpToken}"
                }
            }
        };

        var mcpConfig = new
        {
            mcpServers
        };

        // ADR-0041 / #2096: when concurrent_threads is on, prepend the
        // shared launcher guard to the assembled prompt so the model is
        // told (in the system prompt) not to invoke long-running watchers,
        // bind fixed ports, or mutate shared global state. Composes with
        // the user's prompt — never replaces it.
        var prompt = LauncherPromptFragments.Compose(context.Prompt, context.ConcurrentThreads);

        var workspaceFiles = new Dictionary<string, string>
        {
            ["CLAUDE.md"] = prompt,
            [".mcp.json"] = SerializeMcpConfig(mcpConfig)
        };

        _logger.LogInformation(
            "Prepared Claude Code workspace request ({FileCount} files) for agent {AgentId} thread {ThreadId}",
            workspaceFiles.Count, context.AgentId, context.ThreadId);

        // #1322: SPRING_AGENT_ID, SPRING_MCP_ENDPOINT, SPRING_AGENT_TOKEN are
        // removed — AgentContextBuilder now emits the D1-canonical equivalents
        // (SPRING_AGENT_ID, SPRING_MCP_URL, SPRING_MCP_TOKEN) for every launcher.
        // SPRING_THREAD_ID, SPRING_SYSTEM_PROMPT, SPRING_AGENT_ARGV have no
        // D1-spec equivalent and are retained here as launcher-specific vars.
        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            ["SPRING_SYSTEM_PROMPT"] = prompt,
            // The bridge parses this back into argv via JSON.parse — see
            // src/Cvoya.Spring.AgentSidecar/src/config.ts. Hand-rolling the
            // encoding is forbidden (see issue text); JsonSerializer
            // gives us stable, double-quoted output.
            ["SPRING_AGENT_ARGV"] = JsonSerializer.Serialize(DefaultClaudeArgv),
            // ADR-0041 / #2094: tell the bridge how to bind the platform
            // thread.id (= A2A 0.3 contextId) onto Claude Code's session
            // identifier. First message on a thread → `--session-id <id>`
            // (mints a session with the supplied UUID); subsequent messages
            // → `--resume <id>` (loads the existing session file from disk).
            // The bridge picks create vs resume from a marker file it
            // persists to the workspace volume (see CLAUDE_CONFIG_DIR
            // below) so the answer survives container restart. Both flags
            // verified against `claude --help` (Claude Code 2.1.x).
            [ThreadIdArgCreateEnvVar] = ThreadIdArgCreate,
            [ThreadIdArgResumeEnvVar] = ThreadIdArgResume,
            // ADR-0041 / #2094: anchor Claude Code's session storage on the
            // per-agent workspace volume (D1 § 2.2.1, ADR-0029). Claude
            // writes session files at `<CLAUDE_CONFIG_DIR>/projects/<cwd-mangled>/<sid>.jsonl`;
            // by pointing CLAUDE_CONFIG_DIR under SPRING_WORKSPACE_PATH the
            // session file survives container restart and can be resumed
            // by the next `--resume <sid>` invocation.
            [ClaudeConfigDirEnvVar] = $"{AgentWorkspaceContract.WorkspaceMountPath}/{ClaudeConfigDirRelative}",
            // D3c: canonical path where the per-agent workspace volume is
            // mounted. The dispatcher provisions the volume and adds the
            // -v mount; the env var tells the in-container SDK where to find
            // it (D1 spec § 2.2.1, `SPRING_WORKSPACE_PATH`).
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.WorkspaceMountPath,
        };

        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);

        if (context.OrchestrationTools is { Length: > 0 })
        {
            mcpServers[SpringOrchestrationMcpServerName] = new
            {
                type = "http",
                url = LauncherCallbackEnvironment.BuildOrchestrationMcpUrl(envVars),
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {envVars[AgentCallbackEnvironmentContract.CallbackTokenEnvVar]}"
                }
            };
            workspaceFiles[".mcp.json"] = SerializeMcpConfig(mcpConfig);
        }

        // #1714 step 2: inject the Claude OAuth token into
        // CLAUDE_CODE_OAUTH_TOKEN. The Claude agent runtime is OAuth-only
        // (the project does not run `claude --bare`) — API keys are
        // rejected pre-flight with operator guidance pointing at
        // `claude setup-token` or the Spring Voyage runtime. ANTHROPIC_API_KEY
        // is NEVER injected by this launcher; that env var only flows out
        // of the Spring Voyage launcher when `provider: anthropic`.
        await ResolveRuntimeCredentialAsync(context, envVars, cancellationToken);

        return new AgentLaunchSpec(
            WorkspaceFiles: workspaceFiles,
            EnvironmentVariables: envVars,
            WorkspaceMountPath: WorkspaceMountPath,
            // Empty argv: defer to the agent-base image's ENTRYPOINT (the
            // TypeScript bridge), which reads SPRING_AGENT_ARGV and spawns
            // the real CLI per `message/send`. BYOI conformance path 1.
            Argv: Array.Empty<string>(),
            // Same content as CLAUDE.md / SPRING_SYSTEM_PROMPT — the bridge
            // (PR 5) will pipe this to `claude`'s stdin alongside the per-
            // message user text. Populated here so PR 5 can wire it up
            // without touching the launcher contract again. Carries the
            // concurrent-threads guard prepend (#2096 / ADR-0041) when on.
            StdinPayload: prompt);
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
