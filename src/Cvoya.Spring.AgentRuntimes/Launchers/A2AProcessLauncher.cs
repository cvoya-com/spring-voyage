// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.ModelProviders;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntimeLauncher"/> for the generic <c>a2a-process</c>
/// runtime (ADR-0066) — a long-running, always-on container that hosts an
/// external orchestration engine (e.g. LangGraph) and speaks A2A natively.
///
/// Unlike <see cref="SpringVoyageAgentLauncher"/> this launcher is
/// <b>image-agnostic</b>: it does not pin an argv or any engine-specific
/// behaviour. It stamps the platform env contract (workspace mount, A2A
/// port) and contributes the assembled system prompt as the
/// <c>.spring/system-prompt.md</c> bundle file (ADR-0055 / ADR-0058 §2.2.2),
/// then leaves <see cref="AgentLaunchSpec.Argv"/> empty so the image's own
/// <c>ENTRYPOINT</c>/<c>CMD</c> starts the engine. An engine author ships a
/// conforming A2A image (BYOI path 3) and selects <c>ai.runtime: a2a-process</c>
/// — no per-engine launcher is required.
///
/// The runtime launches <b>without a Dapr sidecar</b>: the dispatcher's
/// <c>useDaprSidecar</c> branch keys on the <c>spring-voyage-agent</c> launcher
/// only, so every other native-A2A launcher (including this one) takes the
/// plain container path.
///
/// Because the engine process <i>is</i> the run loop, these agents are
/// <see cref="AgentHostingMode.Persistent"/>: the existing persistent dispatch
/// path keeps the container warm and routes every subsequent message to the
/// same live endpoint as an event. The per-turn MCP session token is delivered
/// in each <c>message/send</c>'s A2A metadata (the SV Agent SDK surfaces it as
/// <c>Message.mcp_token</c>) rather than baked into the boot env — see ADR-0066
/// §2 and the SDK's <c>IAgentContext</c> (which treats <c>SPRING_MCP_TOKEN</c>
/// as optional for always-on runtimes).
/// </summary>
public class A2AProcessLauncher(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IAgentCallbackEnvironmentBuilder? callbackEnvironmentBuilder = null) : IAgentRuntimeLauncher
{
    /// <summary>Default A2A port the engine container listens on.</summary>
    internal const int DefaultAgentPort = 8999;

    /// <summary>
    /// Workspace-relative path the platform writes the assembled system
    /// prompt to (ADR-0058 §2.2.2). The engine reads it via the SDK's
    /// <c>IAgentContext.system_prompt</c> when it wants the platform prompt;
    /// a purely deterministic engine may ignore it.
    /// </summary>
    internal const string PlatformPromptFilePath = ".spring/system-prompt.md";

    private readonly ILogger _logger = loggerFactory.CreateLogger<A2AProcessLauncher>();

    /// <summary>
    /// Tool-kind identifier. Matches the catalogue runtime entry's
    /// <c>launcher</c> field for every runtime that dispatches through this
    /// launcher.
    /// </summary>
    public const string ToolId = LauncherIds.A2AProcess;

    /// <inheritdoc />
    public string Kind => ToolId;

    /// <inheritdoc />
    /// <remarks>
    /// The engine container is BYOI path 3 (native A2A); the readiness probe
    /// (<c>GET /.well-known/agent.json</c>) is the real liveness signal. The
    /// declarative probe here is a minimal "is the interpreter present" check
    /// mirroring <see cref="SpringVoyageAgentLauncher"/>.
    /// </remarks>
    public IReadOnlyList<ProbeStep> GetProbeSteps(ModelProviderInstallConfig config, string credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new[]
        {
            new ProbeStep(
                Step: ArtefactValidationStep.VerifyingTool,
                Args: new[] { "python", "--version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(10),
                InterpretOutput: (exit, _, stderr) => exit == 0
                    ? StepResult.Succeed()
                    : StepResult.Fail(
                        ArtefactValidationCodes.ToolMissing,
                        $"`python --version` exited with code {exit}. {stderr}".TrimEnd())),
        };
    }

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        _logger.LogInformation(
            "Prepared a2a-process launch request for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

        var envVars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            // AGENT_PORT is the port the in-container engine binds its A2A
            // server to; the dispatcher dials it after readiness.
            ["AGENT_PORT"] = DefaultAgentPort.ToString(),
            // ADR-0055 §5 / ADR-0058: per-member workspace mount. The engine
            // keeps its durable checkpoint here (ADR-0066 §4).
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.BuildMountPath(context.AgentId),
        };

        // Progress/decision callbacks + OTLP activity capture, identical to
        // the other launchers so the engine's sv.runtime.report_progress flows
        // and telemetry land on the same plane.
        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);
        LauncherOtelEnvironment.Add(context, envVars);

        // Optional LLM credential. An a2a-process engine MAY use an LLM (e.g.
        // to interpret a brief) or be purely deterministic. When the agent
        // definition pins a cloud provider, resolve and inject the credential
        // under the provider's conventional env var so the engine's own LLM
        // client can read it. No provider (or Ollama) → nothing to inject.
        await ResolveProviderCredentialAsync(context, envVars, cancellationToken);

        // #3106: WorkingDirectory is intentionally left unset (null) so the
        // engine image's own WORKDIR wins (CWD-independence for BYOI path 3 —
        // e.g. a `WORKDIR /app` image running `python -m orchestrator`). This
        // image-agnostic native-A2A runtime receives everything by env
        // (SPRING_WORKSPACE_PATH / SPRING_MCP_URL / SPRING_MCP_TOKEN) and reads
        // the system prompt by path, so it has no CWD-relative config to
        // discover and must NOT be force-CWD'd to the workspace mount.
        return new AgentLaunchSpec(
            EnvironmentVariables: envVars,
            // Empty argv: defer to the engine image's own ENTRYPOINT/CMD. The
            // image is purpose-built to run the engine's A2A server on
            // AGENT_PORT (BYOI path 3).
            Argv: Array.Empty<string>());
    }

    /// <inheritdoc />
    public string? GetWorkspacePromptFragment() =>
        """
        Your runtime is an A2A-process orchestration engine — a long-running, always-on service that receives every inbound message as an event for as long as the unit lives. Your per-agent workspace is mounted at `$SPRING_WORKSPACE_PATH` and persists across turns and restarts; keep your durable workflow state there. The platform's system prompt, when you want it, is at `$SPRING_WORKSPACE_PATH/.spring/system-prompt.md`. The platform MCP server is reached at `$SPRING_MCP_URL` with a durable, agent-scoped bearer token in `$SPRING_MCP_TOKEN` — a service identity valid for as long as your container runs, so you can call platform tools at any time, including timer- or background-triggered actions between messages, not only while handling a message. The same token is echoed in each inbound message's metadata (`mcpToken`) so you can refresh it if it rotates.
        """;

    /// <inheritdoc />
    public Task<AgentBootstrapContribution> ContributeBundleAsync(
        AgentBootstrapContributionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PlatformPromptFilePath] = context.AssembledSystemPrompt,
        };

        return Task.FromResult(new AgentBootstrapContribution(
            Files: files,
            PlatformFilePaths: new[] { PlatformPromptFilePath }));
    }

    /// <summary>
    /// Resolves the agent's LLM credential (when a cloud provider is pinned)
    /// and injects it under the provider's conventional env var. No-op for an
    /// unset provider or Ollama, and <b>best-effort</b> otherwise: an absent
    /// secret is logged and skipped, not fatal, so a purely deterministic
    /// engine — which declares a model only to satisfy <c>--strict</c>
    /// (ADR-0038) yet makes no LLM calls — still boots. For Anthropic the
    /// runtime co-hosts the Claude CLI and authenticates by OAuth, like
    /// <see cref="ClaudeCodeLauncher"/>: it resolves <c>authMethod: oauth</c>
    /// and injects <c>CLAUDE_CODE_OAUTH_TOKEN</c> (the catalogue edge
    /// <c>a2a-process → anthropic</c> carries oauth to match), which the SDK's
    /// <c>claude --print</c> invocation reads. Other providers map to their
    /// conventional API-key env var. No Dapr — this runtime has no sidecar.
    /// </summary>
    private async Task ResolveProviderCredentialAsync(
        AgentLaunchContext context,
        IDictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
        var provider = context.Provider;
        if (string.IsNullOrWhiteSpace(provider)
            || string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Anthropic is OAuth — the co-hosted Claude CLI reads
        // CLAUDE_CODE_OAUTH_TOKEN; the other providers map to their
        // conventional API-key env var. Keep this in lockstep with the
        // catalogue's a2a-process model-provider edges.
        var (authMethod, providerEnvVar) = provider.ToLowerInvariant() switch
        {
            "anthropic" => (AuthMethod.Oauth, "CLAUDE_CODE_OAUTH_TOKEN"),
            "openai" => (AuthMethod.ApiKey, "OPENAI_API_KEY"),
            "google" => (AuthMethod.ApiKey, "GOOGLE_API_KEY"),
            _ => throw new SpringException(
                    $"a2a-process launcher cannot map provider '{provider}' to a credential env var. " +
                    "Supported providers: anthropic, openai, google. Add the mapping to extend.")
                .WithIssue(code: "ConfigurationIncomplete", source: "configuration"),
        };

        Guid? agentGuid = Guid.TryParse(context.AgentId, out var parsedAgentId) ? parsedAgentId : null;
        Guid? unitGuid = Guid.TryParse(context.UnitId, out var parsedUnitId) ? parsedUnitId : null;

        // ILlmCredentialResolver is scoped; this launcher is a singleton, so
        // resolve through a per-call scope.
        await using var scope = scopeFactory.CreateAsyncScope();
        var credentialResolver = scope.ServiceProvider.GetRequiredService<ILlmCredentialResolver>();
        var resolution = await credentialResolver.ResolveAsync(
            provider.ToLowerInvariant(), authMethod, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            // Best-effort, not fatal (ADR-0066): the engine declares a model to
            // satisfy --strict but may make no LLM calls, so an absent
            // credential must not block boot. An engine that *does* call its
            // model fails later with its own, clearer error.
            _logger.LogInformation(
                "a2a-process agent {AgentId}: no secret '{Secret}' resolved for provider '{Provider}' "
                + "at agent, unit, parent-unit, or tenant scope; launching without {EnvVar}.",
                context.AgentId, resolution.SecretName, provider, providerEnvVar);
            return;
        }

        envVars[providerEnvVar] = resolution.Value!;
        _logger.LogInformation(
            "a2a-process credential resolved from {Source} into {EnvVar} for agent {AgentId}",
            resolution.Source, providerEnvVar, context.AgentId);
    }
}
