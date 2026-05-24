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
        // #2668: the Codex CLI never reads SPRING_SYSTEM_PROMPT — the
        // system prompt is delivered exclusively via AGENTS.md written by
        // ContributeBundleAsync, and the assembled-prompt path inside
        // AgentBootstrapBundleProvider already folds in the
        // ConcurrentThreadsGuard fragment (ADR-0041 / #2096) so the model
        // still sees the concurrency contract.

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            // ADR-0055 §5: per-member workspace mount path. ADR-0057 §3:
            // the long-running A2A sidecar writes the per-turn MCP token
            // to <SPRING_WORKSPACE_PATH>/.spring/bridge/mcp-token
            // before each CLI spawn; the per-turn sidecar-MCP-server-mode
            // child reads it from the same path.
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.BuildMountPath(context.AgentId),
        };

        // ADR-0051: OTLP-ingest env contract stamped here.
        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);

        // #1714 step 2: inject the OpenAI API key into OPENAI_API_KEY.
        await ResolveRuntimeCredentialAsync(context, envVars, cancellationToken);

        _logger.LogInformation(
            "Prepared Codex launch spec for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

        return new AgentLaunchSpec(EnvironmentVariables: envVars);
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
        You are running inside a Debian-based container supervised by the Spring Voyage agent sidecar. The OpenAI Codex CLI (`codex`) is your runtime; the standard image bundles `dotnet`, `gh`, `git`, `node`, and `python3` for general-purpose tooling. Your per-agent workspace is mounted at `$SPRING_WORKSPACE_PATH` and persists across turns and container restarts — anything you clone or write under it stays available next turn. The CLI auto-discovers its system prompt from `AGENTS.md` at the workspace root and its MCP server set from `.mcp.json` (also at the workspace root).
        """;

    /// <inheritdoc />
    public Task<AgentBootstrapContribution> ContributeBundleAsync(
        AgentBootstrapContributionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ADR-0057 §§1, 3: the Codex CLI dials the sidecar's stdio
        // MCP-server mode as a `command`-typed MCP server. See
        // ClaudeCodeLauncher.ContributeBundleAsync for the full
        // rationale; identical shape, only the server-name reuse and
        // the sidecar binary path are shared. No HTTP transport, no
        // Authorization header — the CLI never sees the per-turn
        // token.
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
