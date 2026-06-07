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
///   <item><c>.codex/config.toml</c> — the <c>[mcp_servers.spring-voyage]</c>
///         stdio MCP server entry the Codex CLI discovers under
///         <c>$CODEX_HOME</c> (#3122). Unlike Claude/Gemini, Codex does NOT
///         read <c>.mcp.json</c>.</item>
/// </list>
/// These files are written into the per-agent persistent workspace volume —
/// the single workspace mount at <see cref="AgentWorkspaceContract.WorkspacePathEnvVar"/>
/// (ADR-0029, #2608).
/// <para>
/// <b>Expected container image shape:</b> The image must bundle the Codex CLI
/// and the A2A sidecar from <c>agents/a2a-sidecar/</c>. The sidecar wraps the
/// <c>codex</c> CLI binary, exposing it behind an A2A endpoint. The container
/// must read <c>AGENTS.md</c> from the <c>SPRING_WORKSPACE_PATH</c> mount,
/// read the platform MCP server from <c>$CODEX_HOME/config.toml</c> (the
/// launcher points <c>CODEX_HOME</c> at a workspace-relative dir), and honour
/// the <c>OPENAI_API_KEY</c> environment variable for authentication with the
/// OpenAI API.
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
    /// Workspace-relative directory the launcher points <c>CODEX_HOME</c> at
    /// (#3122). The Codex CLI reads <c>config.toml</c> — and therefore the
    /// platform's <c>[mcp_servers.spring-voyage]</c> entry — from this
    /// directory. A dot-prefixed name keeps it out of agent-author tools that
    /// walk the workspace tree for sources, and anchoring it under the
    /// per-agent workspace volume means the config survives container restart.
    /// </summary>
    internal const string CodexHomeRelative = ".codex";

    /// <summary>
    /// File name (under <see cref="CodexHomeRelative"/>) the Codex CLI reads
    /// its configuration — including the <c>[mcp_servers.&lt;name&gt;]</c>
    /// tables — from. Verified against codex-cli (the <c>CONFIG_TOML_FILE</c>
    /// constant in <c>codex-rs/core/src/config</c>): Codex discovers MCP
    /// servers from <c>$CODEX_HOME/config.toml</c>, NOT from <c>.mcp.json</c>
    /// (#3122).
    /// </summary>
    internal const string CodexConfigFileName = "config.toml";

    /// <summary>
    /// Env var the Codex CLI reads to locate its config home. When set, Codex
    /// reads <c>config.toml</c> (and writes its session / credential state)
    /// under this directory instead of the default <c>~/.codex/</c>. Verified
    /// against codex-cli (<c>find_codex_home</c> in
    /// <c>codex-rs/core/src/config</c>).
    /// </summary>
    internal const string CodexHomeEnvVar = "CODEX_HOME";

    /// <summary>
    /// MCP server entry name — ADR-0054's single <c>spring-voyage</c>
    /// platform MCP server, now sidecar-local per ADR-0057.
    /// </summary>
    internal const string SpringVoyageMcpServerName = "spring-voyage";

    /// <summary>
    /// Env var the A2A sidecar reads to learn how to interpret the Codex
    /// CLI's stdout (<c>src/Cvoya.Spring.AgentSidecar/src/config.ts</c>). Set
    /// to <see cref="OutputFormatStreamJson"/> because <see cref="BaseCodexArgv"/>
    /// runs <c>codex exec --json</c> (#3123); the sidecar parses the Codex
    /// JSONL event stream, surfaces the <c>agent_message</c> item as the
    /// reply, surfaces tool calls as status, and hands the turn's token usage
    /// to the host as A2A task metadata (Codex reports no per-turn USD cost).
    /// </summary>
    internal const string OutputFormatEnvVar = "SPRING_AGENT_OUTPUT_FORMAT";

    /// <summary>
    /// NDJSON stream-json output-format hint value for
    /// <see cref="OutputFormatEnvVar"/>. The sidecar's single shape-driven
    /// stream-json parser recognises Codex's JSONL events alongside the
    /// Claude/Gemini schemas (#3123).
    /// </summary>
    internal const string OutputFormatStreamJson = "stream-json";

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
    ///   <item><c>--json</c> makes <c>codex exec</c> emit Codex's own JSONL
    ///   event stream (<c>thread.started</c> / <c>turn.started</c> /
    ///   <c>item.completed</c> with an <c>agent_message</c> item / and a
    ///   terminal <c>turn.completed</c> carrying token <c>usage</c>) instead
    ///   of bare prose. The sidecar (gated by <see cref="OutputFormatEnvVar"/>
    ///   = <see cref="OutputFormatStreamJson"/>) parses it with the same
    ///   shape-driven stream-json parser that handles Claude/Gemini, surfaces
    ///   the <c>agent_message</c> text as the reply, surfaces
    ///   <c>mcp_tool_call</c> items as tool-call status, and hands the turn's
    ///   token usage to the host. Codex reports token usage but no per-turn
    ///   USD cost — the host treats absence of a positive cost as "free"
    ///   while still recording tokens, exactly as the Gemini path does
    ///   (#3123).</item>
    /// </list>
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
        "--json",
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
            // #3123: BaseCodexArgv runs `codex exec --json`, so tell the
            // sidecar to parse the JSONL event stream — surface the
            // agent_message item as the reply, tool calls as status, and
            // forward the turn's token usage to the host. Paired with the
            // `--json` argv token above.
            [OutputFormatEnvVar] = OutputFormatStreamJson,
            // #3122: point CODEX_HOME at a workspace-relative dir so the Codex
            // CLI reads the platform's `[mcp_servers.spring-voyage]` entry from
            // <CODEX_HOME>/config.toml (written by ContributeBundleAsync).
            // Codex does NOT read `.mcp.json`. Anchoring it under the per-agent
            // workspace volume keeps the config (and Codex's own session /
            // credential state) across container restarts.
            [CodexHomeEnvVar] = $"{workspaceMountNoSlash}/{CodexHomeRelative}",
            // ADR-0055 §5: per-member workspace mount path. ADR-0057 §3:
            // the long-running A2A sidecar writes the per-turn MCP token
            // to <SPRING_WORKSPACE_PATH>/.spring/bridge/work/<thread>/mcp-token
            // before each CLI spawn and points the spawn env's
            // SPRING_MCP_TOKEN_PATH at that file (per-turn isolation, #3000).
            //
            // #3122 / #3018: the Codex CLI does not blanket-propagate its
            // process env to the stdio MCP server it spawns — but its
            // `[mcp_servers.<name>]` config supports an `env_vars` whitelist
            // (verified against codex-cli `create_env_for_mcp_server` in
            // codex-rs/rmcp-client): every name in that list is resolved from
            // Codex's OWN process env and forwarded into the MCP child.
            // ContributeBundleAsync lists SPRING_MCP_TOKEN_PATH there, so the
            // per-turn token path the sidecar set on this spawn env reaches the
            // child — Codex gets full per-turn token isolation, same as
            // Claude/Gemini (the #3018 "no propagation, shared-token fallback
            // only" limitation is lifted). SPRING_WORKSPACE_PATH is forwarded
            // too so the child can resolve the shared-fallback path for turns
            // with no thread id.
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.BuildMountPath(context.AgentId),
        };

        // ADR-0054: OTLP-ingest env contract stamped here.
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
            // `AGENTS.md` from the workspace root (and the bridge spawns the
            // CLI here); its MCP server set comes from `$CODEX_HOME/config.toml`
            // (#3122), which the env var above resolves independent of CWD.
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
        Your runtime is the OpenAI Codex CLI (`codex`). Your per-agent workspace is mounted at `$SPRING_WORKSPACE_PATH` and persists across turns — anything you write under it stays available next turn. The CLI auto-discovers its system prompt from `AGENTS.md` at the workspace root and its MCP server set from `config.toml` under `$CODEX_HOME`.
        """;

    /// <summary>
    /// Workspace-relative path of the Codex config file the bundle writes —
    /// <c>.codex/config.toml</c>. Equals <see cref="CodexHomeRelative"/> +
    /// <see cref="CodexConfigFileName"/>; the launcher points
    /// <see cref="CodexHomeEnvVar"/> at the same <c>.codex</c> dir so the CLI
    /// reads this exact file (#3122).
    /// </summary>
    internal const string CodexConfigPath = CodexHomeRelative + "/" + CodexConfigFileName;

    /// <inheritdoc />
    public Task<AgentBootstrapContribution> ContributeBundleAsync(
        AgentBootstrapContributionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // #3122 / ADR-0057 §§1, 3: the Codex CLI discovers MCP servers from
        // `$CODEX_HOME/config.toml` `[mcp_servers.<name>]` (verified against
        // codex-cli — it does NOT read `.mcp.json`, unlike Claude/Gemini). So
        // the platform's single sidecar-local stdio MCP server (ADR-0054's
        // one `spring-voyage` server) is written as a TOML `[mcp_servers.…]`
        // table here. The shape mirrors Claude's `.mcp.json` stdio entry
        // (command = node, args = [<sidecar>, "mcp"], a static `env` block
        // for SPRING_MCP_PROXY_URL / SPRING_WORKSPACE_PATH) — the CLI spawns
        // `node /opt/.../cli.js mcp` as a child each tool-use round, and that
        // child proxies onto the worker's POST /mcp/ route. No HTTP transport,
        // no Authorization header — the CLI never sees the per-turn token.
        //
        // The per-turn MCP session token is delivered via the `env_vars`
        // whitelist (SPRING_MCP_TOKEN_PATH), NOT the static `env` block: Codex
        // resolves each `env_vars` name from its OWN process env and forwards
        // it to the MCP child (codex-rs `create_env_for_mcp_server`), so the
        // per-turn path the sidecar stamps on the CLI spawn env (#3000) reaches
        // the child. A static `env` value cannot carry a per-turn token — it
        // is fixed at bundle-build time — which is why SPRING_MCP_TOKEN_PATH
        // must ride `env_vars` and not `env`. SPRING_WORKSPACE_PATH is in both:
        // the static `env` guarantees the child can resolve the shared-fallback
        // token path even when the CLI env carries no per-thread pointer (a
        // turn with no thread id).
        var configToml = BuildSpringVoyageMcpConfigToml(
            mcpProxyUrl: context.McpEndpoint,
            workspacePath: AgentWorkspaceContract.BuildMountPath(context.AgentId));

        // AGENTS.md is the Codex CLI's auto-discovered system-prompt
        // file. The bundle provider has composed the per-agent system
        // prompt (platform contract + unit context + agent
        // instructions + equipped skill bundles) via IPromptAssembler
        // and handed it in on AgentBootstrapContributionContext.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENTS.md"] = context.AssembledSystemPrompt,
            [CodexConfigPath] = configToml,
        };

        return Task.FromResult(new AgentBootstrapContribution(
            Files: files,
            PlatformFilePaths: new[] { "AGENTS.md", CodexConfigPath }));
    }

    /// <summary>
    /// Builds the Codex <c>config.toml</c> body carrying the single
    /// <c>[mcp_servers.spring-voyage]</c> stdio server table (#3122).
    /// </summary>
    /// <remarks>
    /// Hand-rendered rather than via a TOML library so
    /// <c>Cvoya.Spring.AgentRuntimes</c> stays dependency-free and the output
    /// is deterministic and unit-pinnable. The shape is fixed (one stdio
    /// server, known keys), so a full TOML serialiser would be over-kill.
    /// String values are emitted as TOML basic strings via
    /// <see cref="TomlBasicString"/> (escaping <c>"</c> and <c>\</c>), which
    /// is sufficient for the workspace paths / URLs / env-var names this table
    /// carries.
    /// </remarks>
    internal static string BuildSpringVoyageMcpConfigToml(string mcpProxyUrl, string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpProxyUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var builder = new System.Text.StringBuilder();
        builder.Append("# Spring Voyage platform MCP server (#3122).\n");
        builder.Append("# Codex discovers MCP servers from this file under $CODEX_HOME — NOT .mcp.json.\n");
        builder.Append('[').Append("mcp_servers.").Append(SpringVoyageMcpServerName).Append("]\n");
        builder.Append("command = ").Append(TomlBasicString(ClaudeCodeLauncher.SidecarNodeBinary)).Append('\n');
        builder.Append("args = [")
            .Append(TomlBasicString(ClaudeCodeLauncher.SidecarBinaryPath))
            .Append(", ")
            .Append(TomlBasicString("mcp"))
            .Append("]\n");
        // env_vars: names forwarded from Codex's own process env into the MCP
        // child. SPRING_MCP_TOKEN_PATH carries THIS turn's per-thread token
        // file path (the sidecar sets it on the spawn env per turn, #3000), so
        // listing it here is what gives Codex per-turn token isolation.
        builder.Append("env_vars = [")
            .Append(TomlBasicString(McpTokenStorePathEnvVar))
            .Append("]\n");
        // Static env block: fixed values known at bundle-build time. The
        // per-turn token path canNOT live here (see ContributeBundleAsync).
        builder.Append('[').Append("mcp_servers.").Append(SpringVoyageMcpServerName).Append(".env]\n");
        builder.Append("SPRING_MCP_PROXY_URL = ").Append(TomlBasicString(mcpProxyUrl)).Append('\n');
        builder.Append("SPRING_WORKSPACE_PATH = ").Append(TomlBasicString(workspacePath)).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Env var the sidecar's MCP-server-mode child reads the per-turn token
    /// file path from (<c>SPRING_MCP_TOKEN_PATH</c>, mirrored from
    /// <c>mcp-token-store.ts</c>'s <c>MCP_TOKEN_PATH_ENV_VAR</c>). Listed in
    /// the Codex MCP server's <c>env_vars</c> whitelist so Codex forwards the
    /// sidecar-set per-turn value to the child (#3122 / #3000).
    /// </summary>
    internal const string McpTokenStorePathEnvVar = "SPRING_MCP_TOKEN_PATH";

    /// <summary>
    /// Renders <paramref name="value"/> as a TOML basic string — wrapped in
    /// double quotes with <c>\</c> and <c>"</c> escaped. The values this
    /// launcher emits (container paths, a localhost URL, env-var names) carry
    /// no control characters, so basic-string escaping is sufficient.
    /// </summary>
    internal static string TomlBasicString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
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
