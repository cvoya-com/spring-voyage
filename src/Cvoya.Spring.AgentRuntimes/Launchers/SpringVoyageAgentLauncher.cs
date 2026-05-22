// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IAgentRuntimeLauncher"/> for the Spring Voyage Agent container. Sets the
/// environment variables the Python Dapr Agent expects: MCP endpoint/token,
/// LLM provider/model, and the assembled system prompt. The Dapr Agent
/// consumes its prompt via <c>SPRING_SYSTEM_PROMPT</c>, so this launcher
/// carries no workspace files. With an empty <see cref="AgentLaunchSpec.WorkspaceFiles"/>
/// the dispatcher emits no <c>/workspace</c> bind mount (#2608), and the
/// container runs from its image's own working directory (e.g. <c>/app</c>
/// for <c>python agent.py</c>). The per-agent persistent volume is the
/// container's single workspace mount, at
/// <see cref="AgentWorkspaceContract.WorkspaceMountPath"/>.
///
/// Unlike <see cref="ClaudeCodeLauncher"/> the Dapr Agent is an A2A-native
/// service and does not need a sidecar adapter — it exposes the A2A endpoint
/// directly. The dispatcher reaches the agent on the container's
/// <c>AGENT_PORT</c> (default 8999).
///
/// PR 4 of the #1087 series wires the launcher to BYOI conformance path 3:
/// the spec sets a non-empty <see cref="AgentLaunchSpec.Argv"/> that bypasses
/// the agent-base bridge entirely and hands control directly to the Python
/// process that already speaks A2A natively.
/// </summary>
public class SpringVoyageAgentLauncher(
    IOptions<OllamaOptions> ollamaOptions,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IAgentCallbackEnvironmentBuilder? callbackEnvironmentBuilder = null) : IAgentRuntimeLauncher
{
    /// <summary>Default A2A port the Dapr Agent listens on.</summary>
    internal const int DefaultAgentPort = 8999;

    /// <summary>
    /// Argv vector that bypasses the agent-base bridge and starts the Dapr
    /// Agent process directly. Matches the CMD declared by
    /// <c>agents/spring-voyage-agent/Dockerfile</c>. BYOI conformance path 3.
    /// </summary>
    /// <remarks>
    /// Issue #1106 verified (2026-04): the upstream <c>dapr-agents 1.0.1</c>
    /// PyPI package does NOT publish a runnable A2A entrypoint module —
    /// <c>dapr_agents/__init__.py</c> exports <c>DurableAgent</c>,
    /// <c>AgentRunner</c>, chat clients, and helpers, but no
    /// <c>dapr_agents.a2a</c> module. The A2A surface is provided by
    /// <c>a2a-sdk[http-server]</c>; agents wire their own ASGI app and
    /// expose it via uvicorn (see <c>agents/spring-voyage-agent/agent.py</c> +
    /// <c>agents/spring-voyage-agent/a2a_server.py</c>). If upstream ever adds a
    /// runnable A2A module, this argv can be swapped for
    /// <c>python -m dapr_agents.&lt;module&gt;</c> without changing the
    /// launcher contract.
    /// </remarks>
    internal static readonly string[] DefaultSpringVoyageAgentArgv = ["python", "agent.py"];

    private readonly ILogger _logger = loggerFactory.CreateLogger<SpringVoyageAgentLauncher>();

    /// <summary>
    /// Tool-kind identifier for this launcher. Matches the catalogue
    /// runtime entry's <c>launcher</c> field for every runtime that
    /// dispatches through this launcher.
    /// </summary>
    public const string ToolId = LauncherIds.SpringVoyageAgent;

    /// <inheritdoc />
    public string Kind => ToolId;

    /// <inheritdoc />
    /// <remarks>
    /// Verify-tool baseline only — the Spring Voyage Agent launches via
    /// <c>python agent.py</c> so the equivalent of "is the binary
    /// present" check is whether <c>python</c> can resolve. Per-provider
    /// credential and model probes migrate alongside the manifest
    /// reshape in PR-1b — see follow-up captured in the Chunk 2a final
    /// report.
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
        _logger.LogInformation(
            "Prepared Spring Voyage Agent launch request for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

        var opts = ollamaOptions.Value;

        // Provider / model selection is YAML-driven via AgentDefinition.Execution:
        // when the definition specifies execution.provider / execution.model those win.
        // Otherwise the launcher falls back to Ollama defaults so existing definitions
        // without the fields continue to work. These env vars map to the Dapr
        // Conversation component name (`llm-{provider}`, ADR-0038) and model
        // metadata consumed by the Python agent; changing provider is a
        // YAML-only change.
        var provider = !string.IsNullOrWhiteSpace(context.Provider) ? context.Provider! : "ollama";
        var model = !string.IsNullOrWhiteSpace(context.Model)
            ? context.Model!
            : opts.DefaultModel ?? "llama3.2:3b";

        // #1322: SPRING_AGENT_ID, SPRING_MCP_ENDPOINT, SPRING_AGENT_TOKEN are
        // removed — AgentContextBuilder now emits the D1-canonical equivalents
        // (SPRING_AGENT_ID, SPRING_MCP_URL, SPRING_MCP_TOKEN) for every launcher.
        //
        // #1327: SPRING_MODEL, SPRING_LLM_PROVIDER, SPRING_LLM_COMPONENT are
        // added to the D1 spec (§ 2.2.1) and emitted here as Dapr-agent-specific
        // vars. AgentContextBuilder emits SPRING_LLM_PROVIDER_URL for all launchers.
        // SPRING_LLM_COMPONENT remains launcher-specific (Dapr Conversation component
        // name) and is not part of the D1 spec.
        //
        // #1328: OLLAMA_ENDPOINT removed — llm-ollama.yaml now reads
        // SPRING_LLM_PROVIDER_URL.
        //
        // SPRING_THREAD_ID and SPRING_SYSTEM_PROMPT have no D1-spec equivalents
        // and are retained as launcher-specific vars.
        // Issue #2493: prepend the always-on ResponseDiscipline fragment
        // (and the conditional ConcurrentThreadsGuard) before the user's
        // assembled prompt. Same composition path the CLI launchers use,
        // so every agent runtime sees the response-discipline instructions
        // — including the Python reference agent that consumes
        // SPRING_SYSTEM_PROMPT directly.
        var assembledPrompt = LauncherPromptFragments.Compose(context.Prompt, context.ConcurrentThreads);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            ["SPRING_SYSTEM_PROMPT"] = assembledPrompt,
            ["SPRING_MODEL"] = model,
            ["SPRING_LLM_PROVIDER"] = provider,
            // AGENT_PORT is the env var the in-container agent.py binds to
            // (see agents/spring-voyage-agent/Dockerfile). DAPR_AGENT_PORT is the
            // contract name introduced by issue #1097 — kept alongside
            // AGENT_PORT for back-compat with existing deployments while
            // PR 5 cuts the dispatcher over to the new field.
            ["AGENT_PORT"] = DefaultAgentPort.ToString(),
            ["DAPR_AGENT_PORT"] = DefaultAgentPort.ToString(),
            // The Python `dapr` SDK defaults its gRPC client deadline to
            // 60 s (`DAPR_API_TIMEOUT_SECONDS`); the Conversation Alpha2
            // unary call inherits that deadline. With ~58 MCP tool
            // schemas in the prompt, llama3.2:3b on CPU takes far longer
            // than 60 s to produce its first response, so the call hits
            // the deadline and returns `DEADLINE_EXCEEDED` /
            // `Received RST_STREAM with error code 8` before the agent's
            // loop can make progress. 600 s gives Ollama on a slow CPU
            // enough headroom for a single LLM turn while still bounding
            // a hung sidecar.
            ["DAPR_API_TIMEOUT_SECONDS"] = "600",
            // D3c: canonical path where the per-agent workspace volume is
            // mounted (D1 spec § 2.2.1, `SPRING_WORKSPACE_PATH`).
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.WorkspaceMountPath,
        };

        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);

        // Issue #2492: OTLP/HTTP+JSON activity-capture plane.
        // Reuses the per-invocation callback token (SPRING_CALLBACK_TOKEN)
        // for OTLP auth — no new credential primitive. The OTLP endpoint
        // sits on the platform's API host at /otlp/v1/, alongside the
        // dispatcher's runtime callback.
        LauncherOtelEnvironment.Add(context, envVars);

        // ADR-0051: sv.messaging.send / sv.messaging.multicast are served by
        // the single platform MCP server (SPRING_MCP_URL / SPRING_MCP_TOKEN)
        // alongside every other sv.* tool. The runtime discovers them via the
        // MCP server's tools/list — there is no separate messaging env var.

        // #1328: OLLAMA_ENDPOINT removed. The Dapr Conversation component YAML
        // (llm-ollama.yaml) now reads SPRING_LLM_PROVIDER_URL, which is
        // emitted by AgentContextBuilder for every launcher. OLLAMA_ENDPOINT is no
        // longer set here.

        // #1714: per-provider credential resolution + Conversation component
        // selection. The Spring Voyage runtime routes to one of N providers
        // (Anthropic / OpenAI / Google / Ollama) — the Conversation component
        // each provider ships pins both the API endpoint and the secret key
        // ref, so the launcher must (a) inject the resolved credential under
        // the provider's expected env-var name so daprd's local-env secret
        // store can read it, and (b) point the agent at the right component
        // by name via SPRING_LLM_COMPONENT.
        await ResolveProviderCredentialAsync(context, provider, envVars, cancellationToken);

        return new AgentLaunchSpec(
            WorkspaceFiles: new Dictionary<string, string>(),
            EnvironmentVariables: envVars,
            // Empty WorkspaceFiles → ContainerConfigBuilder emits no Workspace
            // and the dispatcher creates no bind mount (#2608); the mount path
            // is supplied for vocabulary consistency with the other launchers.
            WorkspaceMountPath: AgentWorkspaceContract.WorkspaceMountPathNoSlash,
            // Non-empty argv: skip the agent-base bridge ENTRYPOINT and
            // hand control directly to the Python process that already
            // speaks A2A on :8999. BYOI conformance path 3.
            Argv: DefaultSpringVoyageAgentArgv,
            // Dapr Agent receives messages via A2A, not stdin.
            StdinPayload: null);
    }

    private async Task ResolveProviderCredentialAsync(
        AgentLaunchContext context,
        string provider,
        IDictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
        // Always pin the conversation component so the Python agent dials
        // the right Dapr Conversation YAML. The component-naming convention
        // is `llm-<provider-id>` (ADR-0038) — set on every dispatch (including
        // Ollama, which has no credential to inject) so agent.py never
        // silently falls back to a stale default.
        envVars["SPRING_LLM_COMPONENT"] = $"llm-{provider.ToLowerInvariant()}";

        // Ollama has no credential to inject; the llm-ollama.yaml
        // component carries a literal "ollama" key. Skip resolution.
        if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The env-var name on the REST path is provider-specific and must match
        // both `eng/dapr/components/delegated-spring-voyage-agent/llm-<provider>.yaml`'s
        // secretKeyRef name AND `ContainerLifecycleManager.CredentialEnvVarsToPropagate`
        // — the daprd sidecar reads the secret from its own process env via the
        // local-env secret store. The mapping is inline here for the v0.1
        // closed provider set; the catalogue's AgentRuntimeProviderEdge
        // carries the same env-var name for the agent-runtime path
        // (per ADR-0038), but Dapr Conversation YAMLs reference the env
        // var by literal name so we keep the inline switch authoritative
        // for the REST path.
        var providerEnvVar = provider.ToLowerInvariant() switch
        {
            "anthropic" => "ANTHROPIC_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "google" => "GOOGLE_API_KEY",
            // #2189: tag (ConfigurationIncomplete, configuration) — this
            // path indicates a deployment / catalogue gap, not a per-call
            // credential issue.
            _ => throw new SpringException(
                $"Spring Voyage launcher cannot map provider '{provider}' to a Conversation REST env var. " +
                $"Supported providers: anthropic, openai, google. Add the mapping (and a matching " +
                $"llm-<provider>.yaml + ContainerLifecycleManager propagation entry) to extend.")
                .WithIssue(code: "ConfigurationIncomplete", source: "configuration"),
        };

        // The Spring Voyage runtime dispatches via Dapr Conversation REST.
        // Resolve the credential through the chain (agent → unit → tenant)
        // and inspect the shape against the REST path before injecting.
        Guid? agentGuid = Guid.TryParse(context.AgentId, out var parsedAgentId)
            ? parsedAgentId
            : null;
        Guid? unitGuid = Guid.TryParse(context.UnitId, out var parsedUnitId)
            ? parsedUnitId
            : null;

        // ILlmCredentialResolver is scoped (it composes the scoped
        // ISecretResolver/SpringDbContext); this launcher is a singleton,
        // so resolve through a per-call scope to honour DI lifetimes.
        // ADR-0038 (#1770): the resolver is keyed on (provider, authMethod).
        // The Spring Voyage runtime always consumes its providers via API
        // key — its catalogue edges all carry authMethod: api-key.
        await using var scope = scopeFactory.CreateAsyncScope();
        var credentialResolver = scope.ServiceProvider
            .GetRequiredService<ILlmCredentialResolver>();
        var resolution = await credentialResolver.ResolveAsync(
            provider.ToLowerInvariant(), AuthMethod.ApiKey, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            // #2189: tag (CredentialMissing, credential).
            throw new SpringException(
                $"Provider '{provider}' requires secret '{resolution.SecretName}' but no value resolved at " +
                $"agent, unit, parent-unit chain, or tenant scope. " +
                $"Configure via the Tenant defaults panel or `spring secret set --scope tenant {resolution.SecretName} <value>`.")
                .WithIssue(code: "CredentialMissing", source: "credential");
        }

        // Strict per-path acceptance (#1714): the Spring Voyage runtime
        // routes `provider: anthropic` via REST, which only accepts
        // Anthropic Platform API keys (sk-ant-api…). OAuth tokens
        // (sk-ant-oat…) belong on the Claude agent-runtime path. The
        // format check is inline in the launcher per ADR-0038 — REST-
        // path acceptance lives on each IModelProviderAdapter (the
        // single-shot path), agent-runtime-path acceptance lives here
        // on the launcher that actually invokes the CLI.
        if (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase)
            && resolution.Value!.StartsWith("sk-ant-oat", StringComparison.Ordinal))
        {
            // #2189: tag (CredentialFormatRejected, credential).
            throw new SpringException(
                $"Spring Voyage routes 'anthropic' via the Dapr Conversation REST path, which does not accept " +
                $"the resolved credential's shape (secret '{resolution.SecretName}' from {resolution.Source}). " +
                $"Either replace the secret with a REST-compatible credential (e.g. an Anthropic Platform API key " +
                $"sk-ant-api…), or switch this agent to a runtime that accepts that shape (e.g. `agent: claude` " +
                $"for an Anthropic OAuth token).")
                .WithIssue(code: "CredentialFormatRejected", source: "credential");
        }

        envVars[providerEnvVar] = resolution.Value!;
    }
}
