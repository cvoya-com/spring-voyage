// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntimeLauncher"/> for Gemini CLI containers. Describes a
/// per-invocation workspace containing:
/// <list type="bullet">
///   <item>The platform's assembled system prompt — written to
///         <c>GEMINI.md</c> when <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode"/>
///         is <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Append"/>
///         (the default; Gemini auto-discovers it as its instructions
///         file) and to <c>.spring/system-prompt.md</c> when the mode is
///         <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace"/>
///         with <c>GEMINI_SYSTEM_MD</c> pointing at the absolute path
///         (<c>GEMINI_SYSTEM_MD</c> is Gemini CLI's only system-prompt
///         override mechanism; <b>replace-only</b> per gemini-cli 0.41.x
///         — there is no append flag, see #2695).</item>
///   <item><c>.gemini/settings.json</c> — MCP server endpoint + bearer token the Gemini agent will dial.</item>
/// </list>
/// These files are written into the per-agent persistent workspace volume —
/// the single workspace mount at <see cref="AgentWorkspaceContract.WorkspacePathEnvVar"/>
/// (ADR-0029, #2608).
/// <para>
/// <b>Expected container image shape:</b> The image must bundle the Gemini CLI
/// and the A2A sidecar from <c>agents/a2a-sidecar/</c>. The sidecar wraps the
/// <c>gemini</c> CLI binary, exposing it behind an A2A endpoint. The container
/// must run Gemini from the <c>SPRING_WORKSPACE_PATH</c> mount so it reads
/// <c>GEMINI.md</c> and project-scoped <c>.gemini/settings.json</c>, and honour
/// the <c>GOOGLE_API_KEY</c> environment variable for authentication with the
/// Google AI API.
/// </para>
/// </summary>
public class GeminiLauncher(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IAgentCallbackEnvironmentBuilder? callbackEnvironmentBuilder = null) : IAgentRuntimeLauncher
{
    /// <summary>
    /// Runtime id whose credential the Gemini launcher injects. Gemini
    /// CLI uses the Google Generative Language API, so the credential is
    /// the Google AI Studio API key (the same secret slot the Spring
    /// Voyage runtime uses for <c>provider: google</c>).
    /// </summary>
    /// <summary>
    /// Provider id this launcher consumes from the catalogue's
    /// <c>(provider, authMethod)</c> edge per ADR-0038. The Gemini agent
    /// runtime accepts the Google provider with the API-key auth method.
    /// </summary>
    internal const string ProviderId = "google";

    /// <summary>Container env var the Gemini CLI reads its API key from.</summary>
    internal const string CredentialEnvVar = "GOOGLE_API_KEY";

    internal const string GeminiSettingsPath = ".gemini/settings.json";

    /// <summary>
    /// Workspace-relative path Gemini CLI auto-discovers as its
    /// instructions file (Append-mode delivery target). Used when
    /// <see cref="AgentLaunchContext.SystemPromptMode"/> is
    /// <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Append"/>
    /// because Gemini has no append flag — auto-discovery of
    /// <c>GEMINI.md</c> is the only Append-mode delivery channel.
    /// </summary>
    internal const string GeminiMdPath = "GEMINI.md";

    /// <summary>
    /// Workspace-relative path the launcher writes the platform's
    /// assembled system prompt to when
    /// <see cref="AgentLaunchContext.SystemPromptMode"/> is
    /// <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace"/>
    /// (#2672 / #2695). Lives under the <c>.spring/</c> namespace per
    /// ADR-0058 §2.2.2. Pointed at by <see cref="GeminiSystemMdEnvVar"/>
    /// on the same launch.
    /// </summary>
    internal const string PlatformPromptFilePath = ".spring/system-prompt.md";

    /// <summary>
    /// Env var Gemini CLI reads to override its default system prompt
    /// with the contents of a file (<b>replace-only</b>; there is no
    /// append flag in gemini-cli 0.41.x — see #2695). Used in
    /// Replace-mode launches to point the CLI at
    /// <see cref="PlatformPromptFilePath"/>; unset in Append-mode
    /// launches, leaving the CLI to auto-discover <c>GEMINI.md</c>.
    /// </summary>
    internal const string GeminiSystemMdEnvVar = "GEMINI_SYSTEM_MD";

    /// <summary>
    /// Bridge env var name carrying the CLI flag that *creates* a session
    /// with a supplied id (ADR-0041 / #2094 / #2103). Read by
    /// `src/Cvoya.Spring.AgentSidecar/src/config.ts:parseThreadBinding`. Kept as
    /// a separate const here (rather than reused from <see cref="ClaudeCodeLauncher"/>)
    /// so the Claude / Gemini launchers stay independently auditable —
    /// they happen to ship identical flag names because both CLIs settled
    /// on the same convention, but that's a coincidence, not a contract.
    /// </summary>
    internal const string ThreadIdArgCreateEnvVar = "SPRING_THREAD_ID_ARG_CREATE";

    /// <summary>
    /// Bridge env var name carrying the CLI flag that *resumes* a session
    /// by id (ADR-0041 / #2094 / #2103).
    /// </summary>
    internal const string ThreadIdArgResumeEnvVar = "SPRING_THREAD_ID_ARG_RESUME";

    /// <summary>
    /// Gemini CLI flag that creates a fresh session with a caller-supplied
    /// id. Documented in `packages/cli/src/config/config.ts` of
    /// google-gemini/gemini-cli as: "Start a new session with a manually
    /// provided UUID." The flag is mutually exclusive with <c>--resume</c>
    /// and <c>--session-file</c> (gemini-cli `mutual-exclusivity.test.ts`).
    /// </summary>
    internal const string ThreadIdArgCreate = "--session-id";

    /// <summary>
    /// Gemini CLI flag that resumes a session by id. Documented in
    /// `docs/cli/cli-reference.md` of google-gemini/gemini-cli:
    /// <c>gemini -r "&lt;session-id&gt;"</c> resumes the session. The long
    /// form is <c>--resume</c> per the same source.
    /// </summary>
    internal const string ThreadIdArgResume = "--resume";

    /// <summary>
    /// Env var Gemini CLI reads to locate its config / session-storage
    /// home directory. Defined in `packages/core/src/utils/paths.ts`
    /// (google-gemini/gemini-cli) — the <c>homedir()</c> helper returns
    /// <c>process.env['GEMINI_CLI_HOME']</c> when set, otherwise
    /// <c>os.homedir()</c>. The CLI then appends <c>.gemini/</c> to that
    /// base for storage (sessions live at
    /// <c>$GEMINI_CLI_HOME/.gemini/tmp/&lt;project-hash&gt;/chats/&lt;sid&gt;</c>),
    /// so pointing this at <see cref="AgentWorkspaceContract.WorkspaceMountPath"/>
    /// puts session files on the per-agent workspace volume and they
    /// survive container restart. Documented under
    /// <c>docs/cli/enterprise.md</c>.
    /// </summary>
    internal const string GeminiCliHomeEnvVar = "GEMINI_CLI_HOME";

    /// <summary>
    /// Argv vector the A2A bridge (agent-base ENTRYPOINT) spawns inside
    /// the Gemini container on every <c>message/send</c>. Encoded as a
    /// JSON array string in <c>SPRING_AGENT_ARGV</c> so the bridge
    /// (<c>src/Cvoya.Spring.AgentSidecar/src/config.ts</c>) can recover the
    /// exact quoting/whitespace via <c>JSON.parse</c> instead of shell
    /// splitting (#1063 history). The bridge appends
    /// <c>[--session-id|--resume, &lt;thread.id&gt;]</c> to this vector
    /// per <see cref="ThreadIdArgCreate"/> / <see cref="ThreadIdArgResume"/>.
    /// </summary>
    /// <remarks>
    /// Verified against the upstream <c>gemini --help</c> surface
    /// (gemini-cli 0.41.x):
    /// <list type="bullet">
    ///   <item><c>--prompt ""</c> activates non-interactive (headless)
    ///   mode. Per <c>--help</c>: "Run in non-interactive (headless) mode
    ///   with the given prompt. Appended to input on stdin (if any)."
    ///   The empty string is the trigger; the actual user message arrives
    ///   via stdin (which the bridge's <c>runAgentBridge({stdin: userText})</c>
    ///   already pipes in — see <c>src/Cvoya.Spring.AgentSidecar/src/bridge.ts</c>).
    ///   Without <c>--prompt</c> the CLI's default is interactive mode and
    ///   it never exits, hanging the bridge — empirically confirmed.</item>
    ///   <item><c>--output-format stream-json</c> emits newline-delimited
    ///   JSON events (<c>init</c> / <c>message</c> / <c>tool_use</c> /
    ///   <c>tool_result</c> / <c>error</c> / <c>result</c>) the dispatcher
    ///   can map to <see cref="Cvoya.Spring.Core.Messaging.StreamEvent"/>s.
    ///   Direct analogue of Claude's <c>--output-format stream-json</c>.</item>
    ///   <item><c>--yolo</c> auto-approves every tool call without an
    ///   interactive confirmation prompt — the container is the sandbox.
    ///   Direct analogue of Claude's <c>--dangerously-skip-permissions</c>.
    ///   Aliased as <c>-y</c>; we keep the long form for legibility in
    ///   process listings.</item>
    ///   <item><c>--skip-trust</c> trusts the workspace for this session
    ///   without requiring the interactive trust prompt — gemini-cli
    ///   refuses headless invocation in an untrusted directory otherwise
    ///   (see <c>https://geminicli.com/docs/cli/trusted-folders/</c>).
    ///   The container's workspace mount is dispatcher-controlled,
    ///   not user-controlled, so trust is implicit at the platform layer.</item>
    /// </list>
    /// <c>--session-id</c> and <c>--resume</c> are NOT included here —
    /// the bridge appends them per-message based on the thread-binding
    /// env vars (<see cref="ThreadIdArgCreateEnvVar"/> /
    /// <see cref="ThreadIdArgResumeEnvVar"/>) so the same default argv
    /// covers both cold-start and resume.
    /// </remarks>
    internal static readonly string[] DefaultGeminiArgv =
    [
        "gemini",
        "--prompt",
        string.Empty,
        "--output-format",
        "stream-json",
        "--yolo",
        "--skip-trust",
    ];

    // ADR-0051: a single platform MCP server serves every sv.* tool —
    // sv.directory.*, sv.memory.*, sv.runtime.*, and sv.messaging.* — under
    // the MCP session token. There is no separate messaging MCP server.
    private const string SpringVoyageMcpServerName = "spring-voyage";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger _logger = loggerFactory.CreateLogger<GeminiLauncher>();

    /// <inheritdoc />
    /// <remarks>
    /// Matches the <c>launcher</c> field on the runtime catalogue's
    /// <c>gemini</c> entry per ADR-0038 decision 2.
    /// </remarks>
    public string Kind => LauncherIds.GeminiCli;

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
                Args: new[] { "gemini", "--version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(10),
                InterpretOutput: (exit, _, stderr) => exit == 0
                    ? StepResult.Succeed()
                    : StepResult.Fail(
                        ArtefactValidationCodes.ToolMissing,
                        $"`gemini --version` exited with code {exit}. {stderr}".TrimEnd())),
        };
    }

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        // #2672 / #2695: Gemini CLI's only system-prompt override
        // mechanism is the `GEMINI_SYSTEM_MD` env var, which is
        // *replace-only* — gemini-cli 0.41.x has no append flag. The
        // asymmetry with the runtime catalogue's two-mode
        // `system_prompt_mode` field is documented in
        // `docs/architecture/agent-runtime.md`:
        //
        // - Append mode (default): no env override; the platform writes
        //   the assembled prompt to `GEMINI.md` and Gemini
        //   auto-discovers it. The CLI's coding-assistant baseline is
        //   preserved.
        // - Replace mode: the platform writes the assembled prompt to
        //   `.spring/system-prompt.md` and points `GEMINI_SYSTEM_MD` at
        //   it; the CLI's baseline is dropped entirely.
        //
        // The assembled-prompt path inside AgentBootstrapBundleProvider
        // already folds in the ConcurrentThreadsGuard fragment
        // (ADR-0041 / #2096) so the model still sees the concurrency
        // contract either way.

        var workspaceMount = AgentWorkspaceContract.BuildMountPath(context.AgentId);
        var workspaceMountNoSlash = AgentWorkspaceContract.BuildMountPathNoSlash(context.AgentId);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            ["SPRING_AGENT_ARGV"] = JsonSerializer.Serialize(DefaultGeminiArgv),
            // ADR-0055 §5: per-member workspace mount path. ADR-0057 §3:
            // the long-running A2A sidecar writes the per-turn MCP token
            // to <SPRING_WORKSPACE_PATH>/.spring/bridge/mcp-token
            // before each CLI spawn; the per-turn sidecar-MCP-server-mode
            // child reads it from the same path.
            [AgentWorkspaceContract.WorkspacePathEnvVar] = workspaceMount,
            // ADR-0041 / #2094 / #2103: thread-binding for Gemini CLI's
            // --session-id / --resume.
            [ThreadIdArgCreateEnvVar] = ThreadIdArgCreate,
            [ThreadIdArgResumeEnvVar] = ThreadIdArgResume,
            // ADR-0041 / #2103: anchor Gemini CLI's config / session storage
            // on the per-member workspace volume.
            [GeminiCliHomeEnvVar] = workspaceMount,
        };

        // #2695: Replace mode points GEMINI_SYSTEM_MD at the platform
        // prompt file under `.spring/` so the CLI drops its own
        // coding-assistant baseline. Append mode leaves the env var
        // unset — auto-discovery of `GEMINI.md` is the only Append-mode
        // delivery channel Gemini supports.
        if (context.SystemPromptMode == Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace)
        {
            envVars[GeminiSystemMdEnvVar] = $"{workspaceMountNoSlash}/{PlatformPromptFilePath}";
        }

        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);

        // #1714 step 2: inject the Google AI Studio API key.
        await ResolveRuntimeCredentialAsync(context, envVars, cancellationToken);

        _logger.LogInformation(
            "Prepared Gemini launch spec for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

        return new AgentLaunchSpec(EnvironmentVariables: envVars);
    }

    /// <inheritdoc />
    /// <remarks>
    /// #2682: runtime-true prose only — names env vars and CLI surface
    /// the launcher itself wires up (workspace mount, MCP discovery
    /// file, session-storage env var) and stays author-agnostic (no
    /// reference to the project clone, GitHub env vars, or per-task
    /// worktree conventions). The prose covers both Append-mode
    /// (auto-discovered `GEMINI.md`) and Replace-mode
    /// (`GEMINI_SYSTEM_MD` pointing at `.spring/system-prompt.md`)
    /// delivery channels (#2695); the platform contract is the same in
    /// both — what changes is whether the CLI's coding-assistant
    /// baseline survives.
    /// </remarks>
    public string? GetWorkspacePromptFragment() =>
        """
        Your runtime is the Google Gemini CLI (`gemini`). Your per-agent workspace is mounted at `$SPRING_WORKSPACE_PATH` and persists across turns — anything you write under it stays available next turn. The CLI auto-discovers its system prompt from `GEMINI.md` at the workspace root (or, when an agent declares `system_prompt_mode: replace`, from the file `$GEMINI_SYSTEM_MD` points at) and its MCP server set from `.gemini/settings.json`; per-thread session state lives under `$GEMINI_CLI_HOME/.gemini/`.
        """;

    /// <inheritdoc />
    public Task<AgentBootstrapContribution> ContributeBundleAsync(
        AgentBootstrapContributionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // #2672 / #2695: pick the prompt file path from the resolved
        // system_prompt_mode. Gemini's `GEMINI_SYSTEM_MD` is
        // replace-only (no append flag in gemini-cli 0.41.x), so the
        // per-mode delivery channel is asymmetric:
        //
        // - Append (default) → write to `GEMINI.md` at the workspace
        //   root; Gemini auto-discovers it as its instructions file.
        // - Replace → write to `.spring/system-prompt.md` under the
        //   `.spring/` namespace (ADR-0058 §2.2.2); PrepareAsync points
        //   `GEMINI_SYSTEM_MD` at the absolute path so the CLI drops
        //   its own baseline. `GEMINI.md` is not written in this mode.
        //
        // The bundle provider has composed the per-agent system prompt
        // (platform contract + unit context + agent instructions +
        // equipped skill bundles) via IPromptAssembler and handed it in
        // on AgentBootstrapContributionContext.AssembledSystemPrompt.
        var systemPromptMode = context.Definition.Execution?.SystemPromptMode
            ?? Cvoya.Spring.Core.Catalog.SystemPromptMode.Append;
        var promptFilePath = systemPromptMode == Cvoya.Spring.Core.Catalog.SystemPromptMode.Replace
            ? PlatformPromptFilePath
            : GeminiMdPath;

        var workspaceMount = AgentWorkspaceContract.BuildMountPath(context.AgentId);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [promptFilePath] = context.AssembledSystemPrompt,
            [GeminiSettingsPath] = JsonSerializer.Serialize(
                BuildGeminiSettings(context.McpEndpoint, workspaceMount), JsonOptions),
        };

        return Task.FromResult(new AgentBootstrapContribution(
            Files: files,
            PlatformFilePaths: new[] { promptFilePath, GeminiSettingsPath }));
    }

    private static object BuildGeminiSettings(string mcpEndpoint, string workspaceMount)
    {
        // ADR-0057 §§1, 3: the Gemini CLI dials the sidecar's stdio
        // MCP-server mode as a `command`-typed MCP server. Each
        // tool-use round spawns `node /opt/.../cli.js mcp` as a child
        // of the CLI; that child reads the per-turn MCP session token
        // from <workspace>/.spring/bridge/mcp-token (written by
        // the long-running A2A sidecar) and proxies onto the worker's
        // POST /mcp/. No HTTP transport, no Authorization header — the
        // CLI never sees the per-turn token.
        var mcpServers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SpringVoyageMcpServerName] = new
            {
                command = ClaudeCodeLauncher.SidecarNodeBinary,
                args = new[] { ClaudeCodeLauncher.SidecarBinaryPath, "mcp" },
                env = new Dictionary<string, string>
                {
                    ["SPRING_MCP_PROXY_URL"] = mcpEndpoint,
                    ["SPRING_WORKSPACE_PATH"] = workspaceMount,
                },
            },
        };

        return new
        {
            mcpServers,
        };
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
        // The Gemini runtime consumes Google via API key.
        var resolution = await credentialResolver.ResolveAsync(
            ProviderId, AuthMethod.ApiKey, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            // #2189: tag (CredentialMissing, credential).
            throw new SpringException(
                $"Gemini agent runtime requires secret '{resolution.SecretName}' but no value resolved at " +
                $"agent, unit, parent-unit chain, or tenant scope. " +
                $"Generate an API key at https://aistudio.google.com/apikey and store it under '{resolution.SecretName}', " +
                $"or configure via the Tenant defaults panel.")
                .WithIssue(code: "CredentialMissing", source: "credential");
        }

        // Google AI Studio API keys are typically AIza-prefixed but the
        // launcher accepts any non-empty value here — Google's API
        // returns 401 on bad format, which the credential-health
        // watchdog already records.
        envVars[CredentialEnvVar] = resolution.Value!;

        _logger.LogInformation(
            "Gemini credential resolved from {Source} into {EnvVar} for agent {AgentId}",
            resolution.Source, CredentialEnvVar, context.AgentId);
    }
}
